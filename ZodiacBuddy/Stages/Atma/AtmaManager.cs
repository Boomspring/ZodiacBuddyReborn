using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using ZodiacBuddy.Stages.Atma.Data;
using Action = System.Action;
using FFXVec3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;
using RelicNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RelicNote;

namespace ZodiacBuddy.Stages.Atma;
/// <summary>
/// Your buddy for the Atma enhancement stage.
/// </summary>
internal class AtmaManager : IDisposable {
    /// <summary>
    /// Initializes a new instance of the <see cref="AtmaManager"/> class.
    /// </summary>
    
    private Vector3 _lastPosition;
    private DateTime _lastMovement = DateTime.Now;
    private const float MinMovementDistance = 0.2f; // Adjust as needed
    private const float NavResetThreshold = 3f;     // Seconds before declaring stuck
    private const int PostTeleportVnavDelayMs = 1500;
    private readonly AdvancedUnstuck _advancedUnstuck;
    public static Action? OnFallbackPathIssued;
    private static Vector3 ToSys(FFXVec3 v) => new(v.X, v.Y, v.Z);

    private bool _monitoringPathing;
    private bool _monitoringUnstuck;
    private bool _restartNavAfterUnstuck;
    private bool _hasQueuedMountTasks;
    private bool _hasEnteredBetweenAreas;
    private bool _awaitingTeleportFromRelicBookClick;
    private enum PathingContext { None, Enemy, Fate, Leve }
    private PathingContext _pathingContext = PathingContext.None;
    private static readonly Dictionary<int, PathingContext> IndexToPathingContext = new()
    {
        [0] = PathingContext.Enemy,
        [1] = PathingContext.None,
        [2] = PathingContext.Fate,
        [3] = PathingContext.Leve
    };
    private enum UnstuckPhase { Idle, AwaitingPathStart, AwaitingFirstMovement, Active }
    private UnstuckPhase _unstuckPhase = UnstuckPhase.Idle;
    private Vector3 _armPos;
    private PathingContext _resumeContext = PathingContext.None;
    public Vector3? CurrentTargetPosition { get; set; }
    public bool IsPathGenerating => VNavmesh.Nav.PathfindInProgress();
    public bool IsPathing => VNavmesh.Path.IsRunning();
    public bool NavReady => VNavmesh.Nav.IsReady();
    private readonly TaskManager _taskManager = new();
    private ushort? _pendingFateId;
    public bool CanAct
    {
        get
        {
            var playerObject = Player.Object;
            if (playerObject == null || playerObject.IsDead || Player.IsAnimationLocked)
                return false;
            var c = Svc.Condition;
            return !c[ConditionFlag.BetweenAreas]
                   && !c[ConditionFlag.BetweenAreas51]
                   && !c[ConditionFlag.OccupiedInQuestEvent]
                   && !c[ConditionFlag.OccupiedSummoningBell]
                   && !c[ConditionFlag.BeingMoved]
                   && !c[ConditionFlag.Casting]
                   && !c[ConditionFlag.Casting87]
                   && !c[ConditionFlag.Jumping]
                   && !c[ConditionFlag.Jumping61]
                   && !c[ConditionFlag.LoggingOut]
                   && !c[ConditionFlag.Occupied]
                   && !c[ConditionFlag.Occupied39]
                   && !c[ConditionFlag.Unconscious]
                   && !c[ConditionFlag.ExecutingGatheringAction]
                   && !c[ConditionFlag.MountOrOrnamentTransition]
                   && (!c[85] || c[ConditionFlag.Gathering]);
        }
    }
    public AtmaManager() 
    {
        _advancedUnstuck = new AdvancedUnstuck();
        _advancedUnstuck.OnUnstuckComplete += OnUnstuckCompleteHandler;

        Service.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "RelicNoteBook", ReceiveEventDetour);
        Svc.Framework.Update += _advancedUnstuck.RunningUpdate;
    }
    /// <inheritdoc/>
    public void Dispose() {
        Svc.Framework.Update -= MonitorUnstuck;
        Svc.Framework.Update -= WaitForBetweenAreasAndExecute;
        Svc.Framework.Update -= MonitorPathingAndDismount;
        Svc.Framework.Update -= _advancedUnstuck.RunningUpdate;
        Service.AddonLifecycle.UnregisterListener(ReceiveEventDetour);
        _advancedUnstuck.OnUnstuckComplete -= OnUnstuckCompleteHandler;
        _advancedUnstuck.Dispose();
    }
    private static uint GetNearestAetheryte(MapLinkPayload mapLink) {
        var closestAetheryteId = 0u;
        var closestDistance = double.MaxValue;

        static float ConvertRawPositionToMapCoordinate(int pos, float scale) {
            var c = scale / 100.0f;
            var scaledPos = pos * c / 1000.0f;

            return (41.0f / c * ((scaledPos + 1024.0f) / 2048.0f)) + 1.0f;
        }

        var aetherytes = Service.DataManager.GetExcelSheet<Aetheryte>();
        var mapMarkers = Service.DataManager.GetSubrowExcelSheet<MapMarker>();

        foreach (var aetheryte in aetherytes) {
            if (!aetheryte.IsAetheryte)
                continue;

            if (aetheryte.Territory.Value.RowId != mapLink.TerritoryType.RowId)
                continue;

            var map = aetheryte.Map.Value;
            var scale = map.SizeFactor;
            var name = map.PlaceName.Value.Name.ExtractText();

            var mapMarker = mapMarkers
	            .SelectMany(markers => markers)
	            .FirstOrDefault(m => m.DataType == 3 && m.DataKey.RowId == aetheryte.RowId);
            
            if (mapMarker.RowId is 0) {
                Service.PluginLog.Debug($"Could not find aetheryte: {name}");
                return 0;
            }

            var aetherX = ConvertRawPositionToMapCoordinate(mapMarker.X, scale);
            var aetherY = ConvertRawPositionToMapCoordinate(mapMarker.Y, scale);

            // Service.PluginLog.Debug($"Aetheryte found: aetheryte.PlaceName.Value! ({aetherX} ,{aetherY})");
            var distance = Math.Pow(aetherX - mapLink.XCoord, 2) + Math.Pow(aetherY - mapLink.YCoord, 2);
            if (!(distance < closestDistance))
                continue;

            closestDistance = distance;
            closestAetheryteId = aetheryte.RowId;
        }
        return closestAetheryteId;
    }
    private static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;

        // Turn SeString-ish residuals into plain text expectations
        s = s.Normalize(NormalizationForm.FormKC)
            .Replace('’', '\'')
            .Replace('“', '"').Replace('”', '"') 
            .Replace('…', '.') 
            .Replace('–', '-') // en dash
            .Replace('—', '-') // em dash
            .Replace('\u00A0', ' '); // NBSP -> space

        // collapse whitespace & trim
        var sb = new StringBuilder(s.Length);
        var wasSpace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!wasSpace) { sb.Append(' '); wasSpace = true; }
            }
            else
            {
                sb.Append(ch);
                wasSpace = false;
            }
        }

        return sb.ToString().Trim().ToLowerInvariant();
    }
    private void ResetRunStateForNewCycle()
    {
        Svc.Framework.Update -= MonitorPathingAndDismount;
        Svc.Framework.Update -= MonitorUnstuck;

        this._monitoringPathing = false;
        this._monitoringUnstuck = false;

        _unstuckPhase = UnstuckPhase.Idle;
        this._restartNavAfterUnstuck = false;

        this._hasEnteredBetweenAreas = false;
        this._hasQueuedMountTasks = false;
        this._awaitingTeleportFromRelicBookClick = false;
        this._pendingFateId = null;
    }
    private void ResetTeleportCycleFlags()
    {
        this._hasEnteredBetweenAreas = false;
        this._hasQueuedMountTasks = false;
        this._awaitingTeleportFromRelicBookClick = false;
    }
    private static readonly Dictionary<string, ushort> FateNameToId = new()
    {
        ["surprise"] = 317,
        ["heroes of the 2nd"] = 424,
        ["return to cinder"] = 430,
        ["bellyful"] = 475,
        ["giant seps"] = 480,
        ["tower of power"] = 486,
        ["the taste of fear"] = 493,
        ["the four winds"] = 499,
        ["black and nburu"] = 516,
        ["good to be bud"] = 517,
        ["another notch on the torch"] = 521,
        ["quartz coupling"] = 540,
        ["the big bagoly theory"] = 543,
        ["taken"] = 552,
        ["breaching north tidegate"] = 569,
        ["breaching south tidegate"] = 571,
        ["the king's justice"] = 577,
        ["schism"] = 587,
        ["make it rain"] = 589,
        ["in spite of it all"] = 604,
        ["the enmity of my enemy"] = 611,
        ["breaking dawn"] = 616,
        ["everything's better"] = 620,
        ["what gored before"] = 628,
        ["rude awakening"] = 632,
        ["air supply"] = 633,
        ["the ceruleum road"] = 642,
    };
    private unsafe void Teleport(uint aetheryteId) {
        if (Player.Object == null) return;
        if (Service.Configuration.DisableTeleport) return;

        Telepo.Instance()->Teleport(aetheryteId, 0);
    }
    private unsafe void ReceiveEventDetour(AddonEvent type, AddonArgs args) {
        try {
            if (args is AddonReceiveEventArgs receiveEventArgs && (AtkEventType)receiveEventArgs.AtkEventType is AtkEventType.ButtonClick) {
                this.ReceiveEvent((AddonRelicNoteBook*)receiveEventArgs.Addon.Address, (AtkEvent*)receiveEventArgs.AtkEvent);
            }
        }
        catch (Exception ex) {
            Service.PluginLog.Error(ex, "Exception during hook: AddonRelicNotebook.ReceiveEvent:Click");
        }
    }
    private unsafe void ReceiveEvent(AddonRelicNoteBook* addon, AtkEvent* eventData)
    {
        if (!Service.Configuration.IsAtmaManagerEnabled)
        {
            Service.Plugin.PrintMessage("Atma Manager is disabled.");
            return;
        }
        
        if (!EzThrottler.Throttle("RelicNoteClick"))
            return;

        var relicNote = RelicNote.Instance();
        if (relicNote == null)
            return;
        var bookId = relicNote->RelicNoteId;
        var index = addon->CategoryList->SelectedItemIndex;

        // Create lists of each type of target node.
        List<AddonRelicNoteBook.TargetNode> EnemyTargetNodeList = new()
        {
            addon->Enemy0,
            addon->Enemy1,
            addon->Enemy2,
            addon->Enemy3,
            addon->Enemy4,
            addon->Enemy5,
            addon->Enemy6,
            addon->Enemy7,
            addon->Enemy8,
            addon->Enemy9
        };
        
        List<AddonRelicNoteBook.TargetNode> DungeonTargetNodeList = new()
        {
            addon->Dungeon0,
            addon->Dungeon1,
            addon->Dungeon2
        };
        
        List<AddonRelicNoteBook.TargetNode> FateTargetNodeList = new()
        {
            addon->Fate0,
            addon->Fate1,
            addon->Fate2
        };
        
        List<AddonRelicNoteBook.TargetNode> LeveTargetNodeList = new()
        {
            addon->Leve0,
            addon->Leve1,
            addon->Leve2
        };

        // Check if the target node is selected.
        BraveTarget selectedTarget = default;
        
        if (selectedTarget.Name.IsNullOrEmpty())
        {
            foreach (var node in EnemyTargetNodeList.Where(node => IsOwnerNode(eventData->Target, node.CheckBox)))
            {
                selectedTarget = BraveBook.GetValue(bookId).Enemies[EnemyTargetNodeList.IndexOf(node)];
                Service.Plugin.TargetWindow.SetTargetNode(node, selectedTarget);
            }
        }

        if (selectedTarget.Name.IsNullOrEmpty())
        {
            foreach (var node in DungeonTargetNodeList.Where(node => IsOwnerNode(eventData->Target, node.CheckBox)))
            {
                selectedTarget = BraveBook.GetValue(bookId).Dungeons[DungeonTargetNodeList.IndexOf(node)];
                Service.Plugin.TargetWindow.SetTargetNode(node, selectedTarget);
            }
        }

        if (selectedTarget.Name.IsNullOrEmpty())
        {
            foreach (var node in FateTargetNodeList.Where(node => IsOwnerNode(eventData->Target, node.CheckBox)))
            {
                selectedTarget = BraveBook.GetValue(bookId).Fates[FateTargetNodeList.IndexOf(node)];
                Service.Plugin.TargetWindow.SetTargetNode(node, selectedTarget);
            }
        }
        
        if (selectedTarget.Name.IsNullOrEmpty())
        {
            foreach (var node in LeveTargetNodeList.Where(node => IsOwnerNode(eventData->Target, node.CheckBox)))
            {
                selectedTarget = BraveBook.GetValue(bookId).Leves[LeveTargetNodeList.IndexOf(node)];
                Service.Plugin.TargetWindow.SetTargetNode(node, selectedTarget);
            }
        }

        // Flag the target on the map
        var destinationPos = selectedTarget.Position;
        var agentMap = AgentMap.Instance();
        if (agentMap == null)
            return;
        agentMap->FlagMarkerCount = 0;
        agentMap->SetFlagMapMarker(destinationPos.TerritoryType.RowId, destinationPos.Map.RowId,
            destinationPos.RawX * destinationPos.Map.Value.SizeFactor / 100000.0f, 
            destinationPos.RawY * destinationPos.Map.Value.SizeFactor / 100000.0f);

        var zoneName = !string.IsNullOrEmpty(selectedTarget.LocationName)
            ? $"{selectedTarget.LocationName}, {selectedTarget.ZoneName}"
            : selectedTarget.ZoneName;

        if (Service.Configuration.BraveEchoTarget)
        {
            var sb = new SeStringBuilder()
                .AddText("Target selected: ")
                .AddUiForeground(SmartCaseUtil.SmartCaseHelper.SmartTitleCase(selectedTarget.Name), 62);

            if (index == 3) // leves
                sb.AddText($" from {selectedTarget.Issuer}");

            sb.AddText($" in {zoneName}.");

            Service.Plugin.PrintMessage(sb.BuiltString);
        }

        if (Service.Configuration.BraveCopyTarget)
        {
            Service.Plugin.PrintMessage($"Copied {selectedTarget.Name} to clipboard.");
            ImGui.SetClipboardText(selectedTarget.Name);

        }
        
        this.ResetRunStateForNewCycle();
        IndexToPathingContext.TryGetValue(index, out _pathingContext);

        if (index != 1)
        {
            if (this._pathingContext == PathingContext.Fate)
            {
                if (FateNameToId.TryGetValue(Normalize(selectedTarget.Name), out var fateId))
                {
                    this._pendingFateId = fateId;
                    Service.PluginLog.Debug(
                        $"[ZBR] Pending FateId set to {this._pendingFateId.Value} for '{selectedTarget.Name}'.");
                }
                else Service.PluginLog.Warning(
                    $"[ZBR] Unknown FATE name '{selectedTarget.Name}' - cannot resolve FateId.");
            }
                
            Service.Plugin.TargetWindow.SetTarget(selectedTarget.Name);

            var aetheryteId = GetNearestAetheryte(destinationPos);
            if (aetheryteId == 0)
            {
                Service.PluginLog.Warning($"Could not find an aetheryte for {zoneName}");
                return;
            }
                
            // Same zone (or teleport disabled): skip teleport and only start vnavmesh.
            if (Service.Configuration.DisableTeleport || Svc.ClientState.TerritoryType == destinationPos.TerritoryType.RowId)
            {
                if (this._pathingContext != PathingContext.Fate ||
                    (_pendingFateId is { } wantId && TryGetLiveFateById(wantId, out _)))
                { 
                    EnqueueMountUp(); // this uses /vnav flyflag, same as the teleport flow
                } 
                return;
            }
                
            this.Teleport(aetheryteId);
            this.ResetTeleportCycleFlags();
            if (this._awaitingTeleportFromRelicBookClick)
                return;

            this._awaitingTeleportFromRelicBookClick = true;
            Svc.Framework.Update += this.WaitForBetweenAreasAndExecute;
            return;
        }

        var cfcId = selectedTarget.ContentsFinderConditionId;
        var territoryId = selectedTarget.Position.TerritoryType.RowId;
        var started = false;
            
        if (AutoDutyIpc.Enabled)
        {
            if (AutoDutyIpc.HasPath(territoryId))
            {
                started = AutoDutyIpc.StartInstance(territoryId, AutoDutyIpc.DutyMode.UnsyncRegular, useBareMode: true);
            }
            else
            {
                Service.PluginLog.Warning($"AutoDuty reports no path for territory {territoryId} ({zoneName}).");
            }
        }

        if (started)
        {
            Service.Plugin.PrintMessage($"AutoDuty: starting unsynced for {selectedTarget.Name}.");
            return;
        }

        AgentContentsFinder.Instance()->OpenRegularDuty(cfcId);
        Service.Plugin.PrintMessage($"AutoDuty unavailable. Opened Duty Finder for {selectedTarget.Name}.");
    }
    private static bool TryGetLiveFateById(ushort fateId, out IFate fate)
    {
        foreach (var f in Svc.Fates)
        {
            if (f.FateId != fateId)
                continue;

            fate = f;
            return true;
        }
        fate = null!;
        return false;
    }
    private void MonitorUnstuck(IFramework _)
    {
        if (Player.Object == null) return;
        switch (_unstuckPhase)
        {
            case UnstuckPhase.Idle:
                return;

            case UnstuckPhase.AwaitingPathStart:
                if (!VNavmesh.Nav.PathfindInProgress()    
                    && VNavmesh.Path.IsRunning()           
                    && VNavmesh.Path.NumWaypoints() > 0)  
                {
                    _armPos = Player.Object.Position;
                    _unstuckPhase = UnstuckPhase.AwaitingFirstMovement;
                }
                return;

            case UnstuckPhase.AwaitingFirstMovement:
                if (Vector3.Distance(_armPos, Player.Object.Position) >= MinMovementDistance)
                {
                    _lastPosition = Player.Object.Position;
                    _lastMovement = DateTime.Now; 
                    _unstuckPhase = UnstuckPhase.Active;
                }
                return;

            case UnstuckPhase.Active:
                break; 
        }
        if (!IsPathing || _advancedUnstuck.IsRunning) return;

        var now = DateTime.Now;
        var currentPos = Player.Object.Position;

        if (Vector3.Distance(_lastPosition, currentPos) >= MinMovementDistance)
        {
            _lastPosition = currentPos;
            _lastMovement = now;
        }
        else if ((now - _lastMovement).TotalSeconds > NavResetThreshold)
        {
            Service.PluginLog.Debug($"AdvancedUnstuck: stuck detected. Moved {Vector3.Distance(_lastPosition, currentPos)} yalms in {(now - _lastMovement).TotalSeconds:F1} seconds.");
            _resumeContext = _pathingContext;
            this._restartNavAfterUnstuck = true;
            _advancedUnstuck.Start();
            _lastMovement = now;
        }
    }

    internal void WaitForBetweenAreasAndExecute(IFramework framework)
    {
        if (!Service.Configuration.IsAtmaManagerEnabled || !this._awaitingTeleportFromRelicBookClick)
            return;

        if (!this._hasEnteredBetweenAreas)
        {
            if (Svc.Condition[ConditionFlag.BetweenAreas])
                this._hasEnteredBetweenAreas = true;
            return;
        }

        if (Svc.Condition[ConditionFlag.BetweenAreas]) return;
        if (!GenericHelpers.IsScreenReady()) return;
        if (this._hasQueuedMountTasks) return;

        this._hasQueuedMountTasks = true;

        if (_pathingContext == PathingContext.Fate)
        {
            if (_pendingFateId is { } wantId)
            {
                Service.PluginLog.Debug($"[ZBR] Post-teleport check for FateId={wantId} (queued={this._hasQueuedMountTasks}).");

                if (TryGetLiveFateById(wantId, out var liveFate))
                {
                    Service.PluginLog.Debug($"[ZBR] FateId={wantId} is present and active. Moving.");
                    EnqueueMountAndFlyTo(liveFate.Position);

                    this._hasQueuedMountTasks = true;
                }
                else
                {
                    Service.PluginLog.Debug("[ZBR] Clicked FATE id not present/active. Holding at aetheryte.");
                    this._hasQueuedMountTasks = true; 
                }
            }
            else
            {
                Service.PluginLog.Warning("[ZBR] No pending FATE id set. Holding at aetheryte.");
                this._hasQueuedMountTasks = true;
            }
        }
        else
        {
            EnqueueMountUp();
        }
        this._awaitingTeleportFromRelicBookClick = false;
        Svc.Framework.Update -= WaitForBetweenAreasAndExecute;
    }
    private unsafe void EnqueueMountAndFlyTo(Vector3 destination)
    {
        this._taskManager.Enqueue(() => NavReady);
        // Mount (skip if already mounted)
        this._taskManager.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted]) return true;
            var am = ActionManager.Instance();
            const uint rouletteId = 9;
            if (am->GetActionStatus(ActionType.GeneralAction, rouletteId) == 0)
                am->UseAction(ActionType.GeneralAction, rouletteId);
            return true;
        });
        this._taskManager.Enqueue(() => _advancedUnstuck.IsRunning || Svc.Condition[ConditionFlag.Mounted]);
        
        this._taskManager.Enqueue(() =>
        {
            // Extra delay after teleport to avoid racing the client state
            this._taskManager.DelayNextImmediate(PostTeleportVnavDelayMs);
            // If mount dropped during the delay, keep waiting
            return Svc.Condition[ConditionFlag.Mounted] || _advancedUnstuck.IsRunning;
        });

        this._taskManager.Enqueue(() =>
        {
            if (!_advancedUnstuck.IsRunning && !Svc.Condition[ConditionFlag.Mounted])
                return false;

            VNavmesh.SimpleMove.PathfindAndMoveTo(destination, true);
            EnqueueUnmountAfterNav();
            return true;
        });
    }
    private unsafe void EnqueueMountUp()
    {
        this._taskManager.Enqueue(() => NavReady);

        // Dont skip mounting
        this._taskManager.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted])
            {
                Service.PluginLog.Debug("Already mounted, skipping mount roulette use.");
                return true;
            }
            var am = ActionManager.Instance();
            const uint rouletteId = 9;
            if (am->GetActionStatus(ActionType.GeneralAction, rouletteId) == 0)
            {
                Service.PluginLog.Debug("Attempting to use mount roulette...");
                if (am->UseAction(ActionType.GeneralAction, rouletteId))
                {
                    Service.PluginLog.Debug("Using mount roulette.");
                }
                else
                {
                    Service.PluginLog.Warning("Failed to use mount roulette.");
                }
            }
            else
            {
                Service.PluginLog.Warning("Mount roulette unavailable.");
            }
            return true;
        });
        this._taskManager.Enqueue(() =>
        {
            if (!this._advancedUnstuck.IsRunning) 
                return Svc.Condition[ConditionFlag.Mounted];

            Service.PluginLog.Debug("Skipping wait for mounted because AdvancedUnstuck active.");
            return true;

        });

        this._taskManager.Enqueue(() =>
        {
            if (!_advancedUnstuck.IsRunning && !Svc.Condition[ConditionFlag.Mounted])
                return false;

            // Extra delay after teleport to avoid racing the client state
            this._taskManager.DelayNextImmediate(PostTeleportVnavDelayMs);

            if (!_advancedUnstuck.IsRunning && !Svc.Condition[ConditionFlag.Mounted])
                return false;

            Chat.ExecuteCommand("/vnav flyflag");
            EnqueueUnmountAfterNav();
            this._hasEnteredBetweenAreas = false;
            this._awaitingTeleportFromRelicBookClick = false;
            this._hasQueuedMountTasks = false;
            return true;
        });
    }
    public void EnqueueUnmountAfterNav()
    {
        _unstuckPhase = UnstuckPhase.AwaitingPathStart;
        StartUnstuckMonitoring();

        Svc.Framework.Update -= MonitorPathingAndDismount;
        this._monitoringPathing = true;
        Svc.Framework.Update += MonitorPathingAndDismount;
    }
    private void MonitorPathingAndDismount(IFramework _)
    {
        if (_advancedUnstuck.IsRunning)
            return;
        if (VNavmesh.Nav.PathfindInProgress() || VNavmesh.Path.IsRunning())
            return;
        if (!this._monitoringPathing)
            return;
        this._monitoringPathing = false;
        Svc.Framework.Update -= MonitorPathingAndDismount;
        if (this._restartNavAfterUnstuck)
        {
            this._restartNavAfterUnstuck = false;
            RestartNavigationToTarget();
        }
        else
        {
            EnqueueDismount();

            this._taskManager.Enqueue(() =>
            {
                if (Svc.Condition[ConditionFlag.Mounted])
                {
                    Service.PluginLog.Debug("[ZodiacBuddy] Player still mounted after dismount tasks. Waiting another tick.");
                    return false;
                }
                if (VNavmesh.Path.IsRunning())
                {
                    Service.PluginLog.Debug("[ZodiacBuddy] Navmesh is still running after dismount tasks. Waiting another tick.");
                    return false;
                }
                Service.PluginLog.Debug("[ZodiacBuddy] Player dismounted and navmesh idle. Unlocking pathing.");
                if (_pathingContext == PathingContext.Enemy)
                {
                    Service.Plugin.TargetWindow.OnAtmaPathingComplete();
                    this._taskManager.Enqueue(() => { this._taskManager.DelayNextImmediate(750); return true; });
                    this._taskManager.Enqueue(() =>
                    {
                        if (VNavmesh.Nav.PathfindInProgress() || VNavmesh.Path.IsRunning())
                            return true;

                        var tWin = Service.Plugin.TargetWindow;
                        var posOpt = tWin.CurrentTargetPosition;
                        if (posOpt is { } ffxPos)
                        {
                            VNavmesh.SimpleMove.PathfindAndMoveTo(ToSys(ffxPos), false);
                        }
                        //else
                        //{
                            // Optional FALLBACK(commed out for if i need it later)
                            //Chat.ExecuteCommand("/vnav moveflag");
                        //}
                        return true;
                    });

                }

                else if (_pathingContext == PathingContext.Fate)
                {
                    this._hasEnteredBetweenAreas = false;
                    this._awaitingTeleportFromRelicBookClick = false;
                    this._hasQueuedMountTasks = false;
                }
                _pathingContext = PathingContext.None;
                _unstuckPhase = UnstuckPhase.Idle;
                StopUnstuckMonitoring();
                Svc.Framework.Update -= MonitorPathingAndDismount;
                return true;
            });
        }
    }
    private void RestartNavigationToTarget()
    {
        VNavmesh.Path.Stop();

        if (_resumeContext != PathingContext.None)
            _pathingContext = _resumeContext;

        switch (_pathingContext)
        {
            case PathingContext.Enemy:
                {
                    var tWin = Service.Plugin.TargetWindow;
                    var posOpt = tWin.CurrentTargetPosition;

                    if (posOpt is { } ffxPos)
                    {
                        var sysPos = ToSys(ffxPos);
                        Service.PluginLog.Debug($"[ZodiacBuddy] Restart nav (Enemy): nudging toward TargetWindow pos {sysPos}.");
                        VNavmesh.SimpleMove.PathfindAndMoveTo(sysPos, false);
                    }
                    else
                    {
                        Service.PluginLog.Debug("[ZodiacBuddy] Restart nav (Enemy): no TargetWindow pos; using /vnav flyflag.");
                        Chat.ExecuteCommand("/vnav moveflag");
                    }
                    break;
                }

            case PathingContext.Fate:
                if (_pendingFateId is { } wantId && TryGetLiveFateById(wantId, out var liveFate))
                {
                    VNavmesh.SimpleMove.PathfindAndMoveTo(liveFate.Position, true);
                }
                break;

            case PathingContext.Leve:
                Chat.ExecuteCommand("/vnav flyflag");
                break;

            default:
                Chat.ExecuteCommand("/vnavmesh moveflag");
                break;
        }
        _unstuckPhase = UnstuckPhase.AwaitingPathStart;
        StartUnstuckMonitoring();

        this._monitoringPathing = true;
        Svc.Framework.Update += MonitorPathingAndDismount;
    }
    private unsafe void EnqueueDismount()
    {
        if (_advancedUnstuck.IsRunning)
        {
            Service.PluginLog.Debug("Skipping dismount because AdvancedUnstuck is active.");
            return;
        }
        var am = ActionManager.Instance();
        this._taskManager.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted])
                am->UseAction(ActionType.Mount, 0);
        }, "Dismount");
        this._taskManager.Enqueue(() =>
        {
            if (_advancedUnstuck.IsRunning)
            {
                Service.PluginLog.Debug("Skipping Wait for not in flight because AdvancedUnstuck active.");
                return true;
            }
            return !Svc.Condition[ConditionFlag.InFlight] && CanAct;
        }, 1000, "Wait for not in flight");
        this._taskManager.Enqueue(() =>
        {
            if (Svc.Condition[ConditionFlag.Mounted])
                am->UseAction(ActionType.Mount, 0);
        }, "Dismount 2");
        this._taskManager.Enqueue(() =>
        {
            if (_advancedUnstuck.IsRunning)
            {
                Service.PluginLog.Debug("Skipping Wait for dismount because AdvancedUnstuck active.");
                return true;
            }
            return !Svc.Condition[ConditionFlag.Mounted] && CanAct;
        }, 1000, "Wait for dismount");
        this._taskManager.Enqueue(() =>
        {
            if (!Svc.Condition[ConditionFlag.Mounted])
                this._taskManager.DelayNextImmediate(500);
        });
    }
    private void OnUnstuckCompleteHandler()
    {
        Service.PluginLog.Debug("Unstuck finished, restarting navigation.");
        RestartNavigationToTarget();
    }
    private void StartUnstuckMonitoring()
    {
        if (!this._monitoringUnstuck)
        {
            this._monitoringUnstuck = true;
            _lastPosition = Player.Object?.Position ?? Vector3.Zero;
            _lastMovement = DateTime.Now;
            Svc.Framework.Update += MonitorUnstuck;
        }
    }
    private void StopUnstuckMonitoring()
    {
        if (this._monitoringUnstuck)
        {
            Svc.Framework.Update -= MonitorUnstuck;
            this._monitoringUnstuck = false;
        }
    }
    static unsafe bool IsOwnerNode(AtkEventTarget* target, AtkComponentCheckBox* checkbox)
            => target == checkbox->AtkComponentButton.OwnerNode;
    }
