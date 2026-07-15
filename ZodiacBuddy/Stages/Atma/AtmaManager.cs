using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
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
using System.Text.RegularExpressions;
using Dalamud.Game.Inventory;
using Dalamud.Game.Text;

namespace ZodiacBuddy.Stages.Atma;
/// <summary>
/// Your buddy for the Atma enhancement stage.
/// </summary>
internal partial class AtmaManager : IDisposable {
    /// <summary>
    /// Initializes a new instance of the <see cref="AtmaManager"/> class.
    /// </summary>

    private static Vector3 ToSys(FFXVec3 v) => new(v.X, v.Y, v.Z);
    private readonly List<uint> _listOfBooks =
    [
        2001298, // Book of Skyfire I
        2001299, // Book of Skyfire II
        2001300, // Book of Netherfire I
        2001301, // Book of Skyfall I
        2001302, // Book of Skyfall II
        2001303, // Book of Netherfall I
        2001304, // Book of Skywind I
        2001305, // Book of Skywind II
        2001306  // Book of Skyearth I
    ];
    private readonly List<uint> _listOfRelics =
    [
        7824, // Curtana Atma
        7825, // Sphairai Atma
        7826, // Bravura Atma
        7827, // Gae Bolg Atma
        7828, // Artemis Bow
        7829, // Thyrus Atma
        7830, // Stardust Rod Atma
        7831, // The Veil of Wiyu Atma
        7832, // Omnilex Atma
        7833, // Holy Shield Atma
        9251, // Yoshimitsu Atma
    ];
    private string? _pendingFateName;
    private enum PathingContext { None, Enemy, Dungeon, Fate, Leve }
    private PathingContext _pathingContext = PathingContext.None;
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
                   && (!c[ConditionFlag.Unknown85] || c[ConditionFlag.Gathering]);
        }
    }
    public static unsafe bool Mount
    {
        get
        {
            if (Svc.Condition[ConditionFlag.Mounted]) return true;
            var am = ActionManager.Instance();
            const uint rouletteId = 9;
            if (am->GetActionStatus(ActionType.GeneralAction, rouletteId) == 0)
                am->UseAction(ActionType.GeneralAction, rouletteId);
            return true;
        }
    }
    public static unsafe bool Dismount
    {
        get
        {
            if (!Svc.Condition[ConditionFlag.Mounted]) return true;
            var am = ActionManager.Instance();
            if (am->GetActionStatus(ActionType.Mount, 0) == 0)
                am->UseAction(ActionType.Mount, 0);
            return true;
        }
    }
    
    public AtmaManager() 
    {
        this.InitializeRelicEventItem();

        Service.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "RelicNoteBook", ReceiveEventDetour);
        Service.ChatGui.ChatMessage += this.OnRelicEnemyKill;
        Service.ChatGui.ChatMessage += this.OnBookChanged;
    }
    /// <inheritdoc/>
    public void Dispose() {
        Service.ChatGui.ChatMessage -= this.OnRelicEnemyKill;
        Service.ChatGui.ChatMessage -= this.OnBookChanged;
        Service.AddonLifecycle.UnregisterListener(ReceiveEventDetour);
    }

    private void OnRelicEnemyKill(IHandleableChatMessage message)
    {
        if (message.LogKind != XivChatType.SystemMessage)
            return;

        var m = EnemyKillRegex().Match(message.Message.TextValue);
        if (!m.Success)
            return;
        
        foreach (var bookRow in Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.RelicNote>())
        {
            if (!bookRow.MonsterNoteTargetCommon.TryGetFirst(
                    mt => string.Equals(m.Groups[1].Value, mt.Value.BNpcName.Value.Singular.ExtractText(),
                        StringComparison.OrdinalIgnoreCase), out var monsterTarget))
                continue;

            this._pathingContext = PathingContext.Enemy;
            this.FlagTargetOnMap(BraveBook.GetMonsterPosition(monsterTarget.RowId));
            Service.Plugin.TargetWindow.SetTarget(m.Groups[1].Value);
            Service.Plugin.TargetWindow.KillCount = m.Groups[2].Value;
            break;
        }

        if (!Service.Plugin.TargetWindow.CompletedObjective)
        {
            // Start the navigation sequence...
        }
    }
    
    private void InitializeRelicEventItem()
    {
        var excelItems = Service.DataManager.GetExcelSheet<EventItem>();
        
        foreach (var i in Service.GameInventory.GetInventoryItems(GameInventoryType.KeyItems))
        {
            var excelItem = excelItems
                .Where(item => this._listOfBooks.Contains(item.RowId))
                .FirstOrNull(item => item.RowId == i.BaseItemId);

            if (!excelItem.HasValue)
                continue;

            Service.Plugin.TargetWindow.RelicBookGameItem = i;
            Service.Plugin.PrintMessage($"Relic: { excelItem.Value.Name.ToString()} (Loaded)");
            break;
        }
    }
    
    private void OnBookChanged(IHandleableChatMessage message)
    {
        if (message.LogKind != XivChatType.SystemMessage)
            return;

        if (BookRemovedRegex().IsMatch(message.Message.TextValue))
        {
            Service.Plugin.TargetWindow.RelicBookGameItem = null;
            Service.Plugin.PrintMessage("Relic Book Removed");
        }
        else if (BookAddedRegex().IsMatch(message.Message.TextValue))
        {
            this.InitializeRelicEventItem();
        }
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

    private void ResetRunStateForNewCycle()
    {
        this._pendingFateName = null;
    }
    private void ResetTeleportCycleFlags()
    {
        
    }
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
            Service.ChatGui.PrintError("[ZodiacBuddy] Atma Manager is disabled.");
            return;
        }
        
        if (!EzThrottler.Throttle("RelicNoteClick"))
            return;

        var relicNote = RelicNote.Instance();
        if (relicNote == null)
            return;
        var bookId = relicNote->RelicNoteId;
        var index = addon->CategoryList->SelectedItemIndex;

        // Check if the target node is selected.
        var braveBook = BraveBook.GetValue(bookId);
        
        if (Service.Configuration.EnableOnlyWhenRelicEquipped)
        {
            var relicId = this._listOfRelics.ElementAtOrDefault(relicNote->RelicId - 1);
            if (Util.GetEquippedItem(0).ItemId != relicId && 
                Util.GetEquippedItem(1).ItemId != relicId)
            {
                Service.ChatGui.PrintError("[ZodiacBuddy] Disabled until you have the correct relic equipped.");
                return;
            }
        }
        
        // Create lists of each type of target node.
        List<AddonRelicNoteBook.TargetNode> enemyTargetNodeList =
        [
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
        ];
        
        if (TrySelectTarget(enemyTargetNodeList, braveBook.Enemies, out var selectedNode, out var selectedTarget) && 
            relicNote->GetMonsterProgress(enemyTargetNodeList.IndexOf(selectedNode!.Value)) == 3)
        {
            Service.ChatGui.PrintError("[ZodiacBuddy] You have already completed the required number of enemy kills.");
            return;
        }

        List<AddonRelicNoteBook.TargetNode> dungeonTargetNodeList =
        [
            addon->Dungeon0,
            addon->Dungeon1,
            addon->Dungeon2
        ];
        
        if (!selectedTarget.HasValue && !selectedTarget.HasValue &&
            TrySelectTarget(dungeonTargetNodeList, braveBook.Dungeons, out selectedNode, out selectedTarget) &&
            relicNote->IsDungeonComplete(dungeonTargetNodeList.IndexOf(selectedNode!.Value)))
        {
            Service.ChatGui.PrintError("[ZodiacBuddy] You have already completed this dungeon.");
            return;
        }

        List<AddonRelicNoteBook.TargetNode> fateTargetNodeList =
        [
            addon->Fate0,
            addon->Fate1,
            addon->Fate2
        ];
        
        if (!selectedTarget.HasValue && !selectedTarget.HasValue &&
            TrySelectTarget(fateTargetNodeList, braveBook.Fates, out selectedNode, out selectedTarget)
            && relicNote->IsFateComplete(fateTargetNodeList.IndexOf(selectedNode!.Value)))
        {
            Service.ChatGui.PrintError("[ZodiacBuddy] You have already completed this FATE.");
            return;
        }
        
        List<AddonRelicNoteBook.TargetNode> leveTargetNodeList =
        [
            addon->Leve0,
            addon->Leve1,
            addon->Leve2
        ];

        if (!selectedTarget.HasValue && !selectedTarget.HasValue &&
            TrySelectTarget(leveTargetNodeList, braveBook.Leves, out selectedNode, out selectedTarget) 
            && relicNote->IsLeveComplete(leveTargetNodeList.IndexOf(selectedNode!.Value)))
        {
            Service.ChatGui.PrintError("[ZodiacBuddy] You have already completed this leve.");
            return;
        }

        if (!selectedTarget.HasValue || !selectedNode.HasValue)
            return;
        
        Service.Plugin.TargetWindow.SetTargetNode(selectedNode.Value, selectedTarget.Value);
        
        this.ResetRunStateForNewCycle();
        this._pathingContext = (PathingContext) index + 1;
        Service.PluginLog.Debug($"Pathing Context: {this._pathingContext}");

        var destinationPos = selectedTarget.Value.Position;
        this.FlagTargetOnMap(destinationPos);

        var zoneName = !string.IsNullOrEmpty(selectedTarget?.LocationName)
            ? $"{selectedTarget?.LocationName}, {selectedTarget?.ZoneName}"
            : selectedTarget?.ZoneName;

        if (Service.Configuration.BraveEchoTarget)
        {
            var sb = new SeStringBuilder()
                .AddText("Target selected: ")
                .AddUiForeground(SmartCaseUtil.SmartCaseHelper.SmartTitleCase(selectedTarget!.Value.Name), 62);

            if (index == 3) // leves
                sb.AddText($" from {selectedTarget?.Issuer}");

            sb.AddText($" in {zoneName}.");

            Service.Plugin.PrintMessage(sb.BuiltString);
        }

        if (Service.Configuration.BraveCopyTarget)
        {
            Service.Plugin.PrintMessage($"Copied {selectedTarget?.Name} to clipboard.");
            ImGui.SetClipboardText(selectedTarget?.Name);
        }
        
        // if (index != 1)
        // {
            // if (this._pathingContext == PathingContext.Fate)
            // {
                // this._pendingFateName = selectedTarget!.Value.Name;
            // }
                
            Service.Plugin.TargetWindow.SetTarget(selectedTarget!.Value.Name);

            var aetheryteId = GetNearestAetheryte(destinationPos);
            if (aetheryteId == 0)
            {
                Service.PluginLog.Warning($"Could not find an aetheryte for {zoneName}");
                return;
            }
                
            // Same zone (or teleport disabled): skip teleport and only start vnavmesh.
            if (Service.Configuration.DisableTeleport || Svc.ClientState.TerritoryType == destinationPos.TerritoryType.RowId)
            {
                // IFate fatePos = null!;
                // if (this._pathingContext != PathingContext.Fate ||
                    // TryGetLiveFateById(this._pendingFateName, out fatePos))
                { 
                    // EnqueueMountUp(fatePos.Position); // this uses /vnav flyflag, same as the teleport flow
                } 
                return;
            }

            this.Teleport(aetheryteId);
            // this.ResetTeleportCycleFlags();
            // if (this._awaitingTeleportFromRelicBookClick)
            //     return;

            // this._awaitingTeleportFromRelicBookClick = true;
            // Svc.Framework.Update += this.WaitForBetweenAreasAndExecute;
            // return;
        // }

        var cfcId = selectedTarget!.Value.ContentsFinderConditionId;
        var territoryId = selectedTarget!.Value.Position.TerritoryType.RowId;
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
            Service.Plugin.PrintMessage($"AutoDuty: starting unsynced for {selectedTarget?.Name}.");
            return;
        }

        AgentContentsFinder.Instance()->OpenRegularDuty(cfcId);
        Service.Plugin.PrintMessage($"AutoDuty unavailable. Opened Duty Finder for {selectedTarget?.Name}.");
        return;

        bool TrySelectTarget(
            List<AddonRelicNoteBook.TargetNode> targetNodes,
            BraveTarget[] targets,
            out AddonRelicNoteBook.TargetNode? matchedNode,
            out BraveTarget? matchedTarget)
        {
            matchedNode = default;
            matchedTarget = default;

            for (var targetIndex = 0; targetIndex < targetNodes.Count; targetIndex++)
            {
                var node = targetNodes[targetIndex];

                if (!IsOwnerNode(eventData->Target, node.CheckBox))
                    continue;

                matchedNode = node;
                matchedTarget = targets[targetIndex];
                return true;
            }

            return false;
        }
    }

    protected unsafe void FlagTargetOnMap(MapLinkPayload position)
    {
        // Flag the target on the map
        var agentMap = AgentMap.Instance();
        if (agentMap == null)
            return;

        agentMap->FlagMarkerCount = 0;
        // if (!Service.Plugin.TargetWindow.CompletedObjective && _pathingContext != PathingContext.None)
        {
            agentMap->SetFlagMapMarker(position.TerritoryType.RowId, position.Map.RowId,
                position.RawX * position.Map.Value.SizeFactor / 100000.0f, 
                position.RawY * position.Map.Value.SizeFactor / 100000.0f);
        }
    }

    private static bool TryGetLiveFateById(string? selectedTarget, out IFate fate)
    {
        foreach (var f in Svc.Fates)
        {
            if (f.Name.ToString() != selectedTarget)
                continue;

            fate = f;
            Service.Plugin.PrintMessage($"Fate '{selectedTarget}' is up. Proceeding.");
            return true;
        }
        fate = null!;
        Service.Plugin.PrintMessage($"Fate '{selectedTarget}' is not live yet. Please wait and try again.");
        return false;
    }
    // private void MonitorUnstuck(IFramework _)
    // {
    //     if (Player.Object == null) return;
    //     switch (_unstuckPhase)
    //     {
    //         case UnstuckPhase.Idle:
    //             return;
    //
    //         case UnstuckPhase.AwaitingPathStart:
    //             if (!VNavmesh.Nav.PathfindInProgress()    
    //                 && VNavmesh.Path.IsRunning()           
    //                 && VNavmesh.Path.NumWaypoints() > 0)  
    //             {
    //                 _armPos = Player.Object.Position;
    //                 _unstuckPhase = UnstuckPhase.AwaitingFirstMovement;
    //             }
    //             return;
    //
    //         case UnstuckPhase.AwaitingFirstMovement:
    //             if (Vector3.Distance(_armPos, Player.Object.Position) >= MinMovementDistance)
    //             {
    //                 _lastPosition = Player.Object.Position;
    //                 _lastMovement = DateTime.Now; 
    //                 _unstuckPhase = UnstuckPhase.Active;
    //             }
    //             return;
    //
    //         case UnstuckPhase.Active:
    //             break; 
    //     }
    //     if (!IsPathing || _advancedUnstuck.IsRunning) return;
    //
    //     var now = DateTime.Now;
    //     var currentPos = Player.Object.Position;
    //
    //     if (Vector3.Distance(_lastPosition, currentPos) >= MinMovementDistance)
    //     {
    //         _lastPosition = currentPos;
    //         _lastMovement = now;
    //     }
    //     else if ((now - _lastMovement).TotalSeconds > NavResetThreshold)
    //     {
    //         Service.PluginLog.Debug($"AdvancedUnstuck: stuck detected. Moved {Vector3.Distance(_lastPosition, currentPos)} yalms in {(now - _lastMovement).TotalSeconds:F1} seconds.");
    //         _resumeContext = _pathingContext;
    //         this._restartNavAfterUnstuck = true;
    //         _advancedUnstuck.Start();
    //         _lastMovement = now;
    //     }
    // }

    // internal void WaitForBetweenAreasAndExecute(IFramework framework)
    // {
    //     if (!Service.Configuration.IsAtmaManagerEnabled || !this._awaitingTeleportFromRelicBookClick)
    //         return;
    //
    //     if (!this._hasEnteredBetweenAreas)
    //     {
    //         if (Svc.Condition[ConditionFlag.BetweenAreas])
    //             this._hasEnteredBetweenAreas = true;
    //         return;
    //     }
    //
    //     if (Svc.Condition[ConditionFlag.BetweenAreas]) return;
    //     if (!GenericHelpers.IsScreenReady()) return;
    //     if (this._hasQueuedMountTasks) return;
    //
    //     this._hasQueuedMountTasks = true;
    //
    //     if (_pathingContext == PathingContext.Fate)
    //     {
    //         Service.PluginLog.Debug($"[ZodiacBuddy] Post-teleport check for Fate (queued={this._hasQueuedMountTasks}).");
    //
    //         if (TryGetLiveFateById(this._pendingFateName, out var liveFate))
    //         {
    //             Service.PluginLog.Debug($"[ZodiacBuddy] FateId={liveFate.FateId} is present and active. Moving.");
    //             this.EnqueueMountUp(liveFate.Position);
    //         }
    //         else
    //         {
    //             Service.PluginLog.Debug("[ZodiacBuddy] Clicked FATE not present/active. Holding at aetheryte.");
    //         }
    //     }
    //     else
    //     {
    //         EnqueueMountUp();
    //     }
    //     this._awaitingTeleportFromRelicBookClick = false;
    //     Svc.Framework.Update -= WaitForBetweenAreasAndExecute;
    // }
    // private unsafe void EnqueueMountUp(Vector3? dest = null)
    // {
    //     var pFloor = dest.HasValue ? VNavmesh.Query.Mesh.PointOnFloor(dest.Value, true, 1.0f) : Player.Position;
    //     var onMesh = VNavmesh.Query.Mesh.IsPointOnMesh(
    //         pFloor,
    //         0.5f,
    //         false
    //     );
    //     
    //     this._taskManager.Enqueue(() => NavReady);
    //
    //     // Dont skip mounting
    //     this._taskManager.Enqueue(() =>
    //     {
    //         if (Svc.Condition[ConditionFlag.Mounted])
    //         {
    //             Service.PluginLog.Debug("Already mounted, skipping mount roulette use.");
    //             return true;
    //         }
    //         var am = ActionManager.Instance();
    //         const uint rouletteId = 9;
    //         if (am->GetActionStatus(ActionType.GeneralAction, rouletteId) == 0)
    //         {
    //             Service.PluginLog.Debug("Attempting to use mount roulette...");
    //             if (am->UseAction(ActionType.GeneralAction, rouletteId))
    //             {
    //                 Service.PluginLog.Debug("Using mount roulette.");
    //             }
    //             else
    //             {
    //                 Service.PluginLog.Warning("Failed to use mount roulette.");
    //             }
    //         }
    //         else
    //         {
    //             Service.PluginLog.Warning("Mount roulette unavailable.");
    //         }
    //         return true;
    //     });
    //     this._taskManager.Enqueue(() =>
    //     {
    //         if (!this._advancedUnstuck.IsRunning) 
    //             return Svc.Condition[ConditionFlag.Mounted];
    //
    //         Service.PluginLog.Debug("Skipping wait for mounted because AdvancedUnstuck active.");
    //         return true;
    //
    //     });
    //
    //     this._taskManager.Enqueue(() =>
    //     {
    //         if (!_advancedUnstuck.IsRunning && !Svc.Condition[ConditionFlag.Mounted])
    //             return false;
    //
    //         // Extra delay after teleport to avoid racing the client state
    //         this._taskManager.DelayNextImmediate(PostTeleportVnavDelayMs);
    //
    //         if (!_advancedUnstuck.IsRunning && !Svc.Condition[ConditionFlag.Mounted])
    //             return false;
    //
    //         if (dest.HasValue)
    //         {
    //             VNavmesh.SimpleMove.PathfindAndMoveTo(pFloor, !onMesh);
    //         }
    //         else
    //         {
    //             Chat.ExecuteCommand("/vnav flyflag");
    //         }
    //         EnqueueUnmountAfterNav();
    //         this._hasEnteredBetweenAreas = false;
    //         this._awaitingTeleportFromRelicBookClick = false;
    //         this._hasQueuedMountTasks = false;
    //         return true;
    //     });
    // }
    // public void EnqueueUnmountAfterNav()
    // {
    //     _unstuckPhase = UnstuckPhase.AwaitingPathStart;
    //     StartUnstuckMonitoring();
    //
    //     Svc.Framework.Update -= MonitorPathingAndDismount;
    //     this._monitoringPathing = true;
    //     Svc.Framework.Update += MonitorPathingAndDismount;
    // }
    // private void MonitorPathingAndDismount(IFramework _)
    // {
    //     if (_advancedUnstuck.IsRunning)
    //         return;
    //     if (VNavmesh.Nav.PathfindInProgress() || VNavmesh.Path.IsRunning())
    //         return;
    //     if (!this._monitoringPathing)
    //         return;
    //     this._monitoringPathing = false;
    //     Svc.Framework.Update -= MonitorPathingAndDismount;
    //     if (this._restartNavAfterUnstuck)
    //     {
    //         this._restartNavAfterUnstuck = false;
    //         RestartNavigationToTarget();
    //     }
    //     else
    //     {
    //         EnqueueDismount();
    //
    //         this._taskManager.Enqueue(() =>
    //         {
    //             if (Svc.Condition[ConditionFlag.Mounted])
    //             {
    //                 Service.PluginLog.Debug("[ZodiacBuddy] Player still mounted after dismount tasks. Waiting another tick.");
    //                 return false;
    //             }
    //             if (VNavmesh.Path.IsRunning())
    //             {
    //                 Service.PluginLog.Debug("[ZodiacBuddy] Navmesh is still running after dismount tasks. Waiting another tick.");
    //                 return false;
    //             }
    //             Service.PluginLog.Debug("[ZodiacBuddy] Player dismounted and navmesh idle. Unlocking pathing.");
    //             if (_pathingContext == PathingContext.Enemy)
    //             {
    //                 Service.Plugin.TargetWindow.OnAtmaPathingComplete();
    //                 this._taskManager.Enqueue(() => { this._taskManager.DelayNextImmediate(PostTeleportVnavDelayMs); return true; });
    //                 this._taskManager.Enqueue(() =>
    //                 {
    //                     if (VNavmesh.Nav.PathfindInProgress() || VNavmesh.Path.IsRunning())
    //                         return true;
    //
    //                     if (Service.Plugin.TargetWindow.CurrentTargetPosition is { } ffxPos)
    //                     {
    //                         VNavmesh.SimpleMove.PathfindAndMoveTo(ToSys(ffxPos), false);
    //                     }
    //                     return true;
    //                 });
    //
    //             }
    //
    //             else if (_pathingContext == PathingContext.Fate)
    //             {
    //                 this._hasEnteredBetweenAreas = false;
    //                 this._awaitingTeleportFromRelicBookClick = false;
    //                 this._hasQueuedMountTasks = false;
    //             }
    //             _pathingContext = PathingContext.None;
    //             _unstuckPhase = UnstuckPhase.Idle;
    //             StopUnstuckMonitoring();
    //             Svc.Framework.Update -= MonitorPathingAndDismount;
    //             return true;
    //         });
    //     }
    // }
    // private void RestartNavigationToTarget()
    // {
    //     VNavmesh.Path.Stop();
    //
    //     if (_resumeContext != PathingContext.None)
    //         _pathingContext = _resumeContext;
    //
    //     switch (_pathingContext)
    //     {
    //         case PathingContext.Enemy:
    //             {
    //                 if (Service.Plugin.TargetWindow.CurrentTargetPosition is { } ffxPos)
    //                 {
    //                     EnqueueMountUp(ffxPos);
    //                 }
    //                 else
    //                 {
    //                     Service.PluginLog.Debug("[ZodiacBuddy] Restart nav (Enemy): no TargetWindow pos; using /vnav moveflag.");
    //                     Chat.ExecuteCommand("/vnav moveflag");
    //                 }
    //             }
    //             break;
    //         case PathingContext.Fate:
    //             if (TryGetLiveFateById(this._pendingFateName, out var liveFate))
    //             {
    //                 EnqueueMountUp(liveFate.Position);
    //             }
    //             break;
    //
    //         case PathingContext.Leve:
    //             Chat.ExecuteCommand("/vnav flyflag");
    //             break;
    //
    //         default:
    //             Chat.ExecuteCommand("/vnavmesh moveflag");
    //             break;
    //     }
    //     _unstuckPhase = UnstuckPhase.AwaitingPathStart;
    //     StartUnstuckMonitoring();
    //
    //     this._monitoringPathing = true;
    //     Svc.Framework.Update += MonitorPathingAndDismount;
    // }
    // private unsafe void EnqueueDismount()
    // {
    //     if (_advancedUnstuck.IsRunning)
    //     {
    //         Service.PluginLog.Debug("Skipping dismount because AdvancedUnstuck is active.");
    //         return;
    //     }
    //     var am = ActionManager.Instance();
    //     this._taskManager.Enqueue(() => Dismount, "Dismount");
    //     this._taskManager.Enqueue(() =>
    //     {
    //         if (_advancedUnstuck.IsRunning)
    //         {
    //             Service.PluginLog.Debug("Skipping Wait for not in flight because AdvancedUnstuck active.");
    //             return true;
    //         }
    //         return !Svc.Condition[ConditionFlag.InFlight] && CanAct;
    //     }, 1000, "Wait for not in flight");
    //     this._taskManager.Enqueue(() => Dismount, "Dismount 2");
    //     this._taskManager.Enqueue(() =>
    //     {
    //         if (_advancedUnstuck.IsRunning)
    //         {
    //             Service.PluginLog.Debug("Skipping Wait for dismount because AdvancedUnstuck active.");
    //             return true;
    //         }
    //         return !Svc.Condition[ConditionFlag.Mounted] && CanAct;
    //     }, 1000, "Wait for dismount");
    //     this._taskManager.Enqueue(() =>
    //     {
    //         if (!Svc.Condition[ConditionFlag.Mounted])
    //             this._taskManager.DelayNextImmediate(500);
    //     });
    // }
    // private void OnUnstuckCompleteHandler()
    // {
    //     Service.PluginLog.Debug("Unstuck finished, restarting navigation.");
    //     RestartNavigationToTarget();
    // }
    // private void StartUnstuckMonitoring()
    // {
    //     if (!this._monitoringUnstuck)
    //     {
    //         this._monitoringUnstuck = true;
    //         _lastPosition = Player.Object?.Position ?? Vector3.Zero;
    //         _lastMovement = DateTime.Now;
    //         Svc.Framework.Update += MonitorUnstuck;
    //     }
    // }
    // private void StopUnstuckMonitoring()
    // {
    //     if (this._monitoringUnstuck)
    //     {
    //         Svc.Framework.Update -= MonitorUnstuck;
    //         this._monitoringUnstuck = false;
    //     }
    // }
    private static unsafe bool IsOwnerNode(AtkEventTarget* target, AtkComponentCheckBox* checkbox)
            => target == checkbox->AtkComponentButton.OwnerNode;
    
    [GeneratedRegex(@"^Record of (.+?) kill \((\d+\/\d+)\) added for .*$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-GB")]
    private static partial Regex EnemyKillRegex();
    
    [GeneratedRegex(@"^You have obtained a book from the Trials of the Braves\. The objectives therein can be verified by using the item in the Key Items menu\.$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-GB")]
    private static partial Regex BookAddedRegex();
    
    [GeneratedRegex(@"^You throw away a book from the Trials of the Braves\.$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-GB")]
    private static partial Regex BookRemovedRegex();
}
