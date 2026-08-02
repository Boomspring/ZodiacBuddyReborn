using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Command;
using Dalamud.Game.Inventory;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.DalamudServices.Legacy;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using ZodiacBuddy.SmartCaseUtil;
using RelicNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RelicNote;

namespace ZodiacBuddy.Stages.Atma;

/// <summary>
/// Your buddy for the Atma enhancement stage.
/// </summary>
public partial class AtmaManager : IDisposable 
{
    private readonly WindowSystem _windowSystem;
    
    // Atma Stage Viewer
    private readonly AtmaWindow _atmaWindow;
    private const string AtmaStageViewerCommand = "/atma";

    // Cached data
    public GameInventoryItem? RelicBookGameItem;
    private BraveBook? _currentBook;
    private bool _usingCorrectRelic;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    
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
    
    // Cancellation support
    internal CancellationTokenSource? Cts;

    public AtmaManager(IDalamudPluginInterface pluginInterface)
    {
        // Initialise Services and Listeners
        pluginInterface.Create<Service>();
        Service.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "RelicNoteBook", ReceiveRelicNoteBookEvent);
        Service.AtmaManager = this;
        Service.Configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();

        // Commands
        Service.CommandManager.AddHandler(AtmaStageViewerCommand, new CommandInfo(OpenAtmaWindow)
        {
            HelpMessage = "Open the Atma Stage Viewer.",
            ShowInHelp = true,
        });

        // Windowing
        _windowSystem = new WindowSystem("AtmaManager");
        this._atmaWindow = new AtmaWindow();
        _windowSystem.AddWindow(this._atmaWindow);
        Service.Interface.UiBuilder.Draw += _windowSystem.Draw;
        
        // Chat
        Service.ChatGui.ChatMessage += OnRelicEnemyKill;
        Service.ChatGui.ChatMessage += OnRelicFateOrLeveKill;
        Service.ChatGui.ChatMessage += OnBookChanged;

        // Relic Event Item
        Service.ClientState.Login += InitializeRelicEventItem;
        if (Service.ClientState.IsLoggedIn)
        {
            InitializeRelicEventItem();
        }
        
        Service.Framework.Update += this._atmaWindow.FindNearest;
        Service.Framework.Update += DoesEquippedContainExpectedRelic;
    }

    public void Dispose()
    {
        Cts?.Dispose();
        Service.Framework.Update -= DoesEquippedContainExpectedRelic;
        Service.Framework.Update -= this._atmaWindow.FindNearest;
        if (VNavmesh.Path.IsRunning()) VNavmesh.Path.Stop();
        if (VNavmesh.Nav.PathfindInProgress()) VNavmesh.Nav.PathfindCancelAll();
        Service.ClientState.Login -= InitializeRelicEventItem;
        Service.ChatGui.ChatMessage -= OnBookChanged;
        Service.ChatGui.ChatMessage -= OnRelicFateOrLeveKill;
        Service.ChatGui.ChatMessage -= OnRelicEnemyKill;
        Service.Interface.UiBuilder.Draw -= _windowSystem.Draw;
        _windowSystem.RemoveAllWindows();
        Service.CommandManager.RemoveHandler(AtmaStageViewerCommand);
        Service.AddonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "RelicNoteBook", ReceiveRelicNoteBookEvent);
    }

    private void OpenAtmaWindow(string command, string args)
    {
        this._atmaWindow.IsOpen = true;
    }

    // --------------------- Command --------------------- /
    
    private unsafe void ReceiveRelicNoteBookEvent(AddonEvent type, AddonArgs args)
    {
        // First, cleanly check if the event is for us
        if (args is not AddonReceiveEventArgs
            { 
                AddonName: "RelicNoteBook", AtkEventType: AddonEventType.ButtonClick 
            } receiveEventArgs) return;

        if (AutoDuty.Enabled && AutoDuty.IsNavigating())
        {
            Service.ChatGui.PrintError($"[ZodiacBuddy] AutoDuty is currently in progress. Please wait until it's finished.");
            return;
        }
        
        // We know it's for us, so let's create an object we can go over
        GetRelicNoteBookVariables(receiveEventArgs, out var braveBook, out var braveTarget, out var completion, out var total);
        
        if (!_usingCorrectRelic)
        {
            Service.ChatGui.PrintError($"[ZodiacBuddy] Weapon used does not match book.");
            return;
        }

        if (completion >= total)
        {
            Service.ChatGui.PrintError($"[ZodiacBuddy] {SmartCaseHelper.SmartTitleCase(braveTarget.Name)} already completed.");
            return; 
        }

        this._atmaWindow.Target = braveTarget;
        this._atmaWindow.Completion = completion;
        this._atmaWindow.Total = total;
        _currentBook = braveBook;

        if (braveTarget.ContentsFinderConditionId > 0)
        {
            StartAutoDuty(braveTarget);
            return;
        }

        MarkFlagAndFly(braveTarget);
    }
    
    private void OnRelicEnemyKill(IHandleableChatMessage message)
    {
        if (message.LogKind != XivChatType.SystemMessage)
            return;

        var m = EnemyKillRegex().Match(message.Message.TextValue);
        if (!m.Success)
            return;

        if (!_currentBook.HasValue) return;
        
        Service.PluginLog.Debug($"{SmartCaseHelper.SmartTitleCase(m.Groups[1].Value)} killed.");

        var enemyCheck = _currentBook.Value.Enemies
            .FirstOrNull(target => target.Name == m.Groups[1].Value);
        var dungeonCheck = _currentBook.Value.Dungeons
            .FirstOrNull(target => target.Name == m.Groups[1].Value);

        if (enemyCheck.HasValue) _atmaWindow.Target = enemyCheck;
        if (dungeonCheck.HasValue) _atmaWindow.Target = dungeonCheck;
        _atmaWindow.Completion = byte.Parse(m.Groups[2].Value[..1]);
        _atmaWindow.Total = byte.Parse(m.Groups[2].Value.Substring(2, 1));

        if (_atmaWindow.Completion != _atmaWindow.Total) return;
        CancelPathfinding();
    }
    
    private void OnRelicFateOrLeveKill(IHandleableChatMessage message)
    {
        if (message.LogKind != XivChatType.SystemMessage)
            return;

        var a = FateCompletionRegex().Match(message.Message.TextValue);
        var b = LeveCompletionRegex().Match(message.Message.TextValue);
        if (!a.Success && !b.Success)
            return;

        if (!_currentBook.HasValue) return;

        if (a.Success && _atmaWindow.Target?.FateId == 0) _atmaWindow.Target = null;
        if (b.Success && _atmaWindow.Target?.Issuer.IsNullOrEmpty() == true) _atmaWindow.Target = null;
        _atmaWindow.Completion = 1;
        _atmaWindow.Total = 1;
    }
    
    private void OnBookChanged(IHandleableChatMessage message)
    {
        if (message.LogKind != XivChatType.SystemMessage)
            return;

        if (BookRemovedRegex().IsMatch(message.Message.TextValue))
        {
            this._atmaWindow.Target = null;
            this._atmaWindow.Completion = 0;
            this._atmaWindow.Total = 0;
            RelicBookGameItem = null;
            Service.PluginLog.Info("Relic Book Removed");
        }
        else if (BookAddedRegex().IsMatch(message.Message.TextValue))
        {
            InitializeRelicEventItem();
        }
    }

    private static void StartAutoDuty(BraveTarget braveTarget)
    {
        var started = false;
        var territoryId = braveTarget.Position.TerritoryType.RowId;
        
        if (AutoDuty.Enabled)
        {
            if (AutoDuty.ContentHasPath(territoryId))
            {
                started = AutoDuty.StartInstance(braveTarget.Position.TerritoryType.RowId);
            }
            else
            {
                Service.PluginLog.Warning($"AutoDuty reports no path for {braveTarget.Position.PlaceName}.");
            }
        }

        if (started)
        {
            Service.ChatGui.Print($"[ZodiacBuddy] AutoDuty: starting unsynced for {braveTarget.Position.PlaceName}.");
            return;
        }

        unsafe
        {
            AgentContentsFinder.Instance()->OpenRegularDuty(braveTarget.ContentsFinderConditionId);
        }

        Service.ChatGui.PrintError($"[ZodiacBuddy] AutoDuty unavailable. Opened Duty Finder for {braveTarget.Position.PlaceName}.");
    }

    // --------------------- Routine --------------------- /

    internal void MarkFlagAndFly(BraveTarget braveTarget)
    {
        FlagTargetOnMap(braveTarget.Position);

        CancelPathfinding();
        Cts = new CancellationTokenSource();

        Task.Run(() => FlyRoutineAsync(braveTarget, Cts.Token)).ContinueWith(_ =>
        {
            CancelPathfinding();
        });
    }
    
    private async Task<bool> FlyRoutineAsync(BraveTarget braveTarget, CancellationToken token)
    {
        if (!VNavmesh.Enabled)
        {
            Service.PluginLog.Error("Navmesh not enabled.");
            return false;
        }

        if (Svc.Condition[ConditionFlag.InCombat])
        {
            Service.PluginLog.Info("In combat.");
            return false;
        }
        
        try
        {
            // Teleport conditions to wait until you're in the right territory
            if (Player.Territory.RowId != braveTarget.Position.TerritoryType.RowId)
            {
                TeleportToNearestAetheryte(braveTarget.Position);
                if (!await WaitForConditions(() => Svc.Condition[ConditionFlag.BetweenAreas], true, token))
                    return false;
                if (!await WaitForConditions(() => Svc.Condition[ConditionFlag.BetweenAreas], false, token))
                    return false;
                if (!await WaitForConditions(GenericHelpers.IsScreenReady, true, token)) return false;
            }

            // Check if fate is running
            if (braveTarget.FateId > 0 && !IsFateRunning(braveTarget))
                return false;

            // Mount if not already occupy
            if (!Svc.Condition[ConditionFlag.Mounted])
                await WaitForMountAsync(token);

            Service.PluginLog.Info("Mapped flag on map.");

            if (VNavmesh.Path.IsRunning()) VNavmesh.Path.Stop();
            if (VNavmesh.Nav.PathfindInProgress()) VNavmesh.Nav.PathfindCancelAll();
            await WaitForConditions(VNavmesh.Path.IsRunning, false, token);
            await WaitForConditions(VNavmesh.Nav.PathfindInProgress, false, token);
            var target = Vector3.Zero;
            while (target == Vector3.Zero)
            {
                target = await Svc.Framework.RunOnTick(() =>
                {
                    try
                    {
                        return VNavmesh.Query.Mesh.FlagToPoint();
                    }
                    catch (NullReferenceException)
                    {
                        return Vector3.Zero;
                    }
                }, cancellationToken: token);
            }

            VNavmesh.Query.Mesh.PointOnFloor(target, true, 1f);

            Service.PluginLog.Info("Point identified. Starting movement.");

            // Fly to the flag
            var flyToAsyncResult = await MoveToAsync(target, token);

            if (VNavmesh.Path.IsRunning()) VNavmesh.Path.Stop();
            if (VNavmesh.Nav.PathfindInProgress()) VNavmesh.Nav.PathfindCancelAll();
            await WaitForConditions(VNavmesh.Path.IsRunning, false, token);
            await WaitForConditions(VNavmesh.Nav.PathfindInProgress, false, token);

            if (flyToAsyncResult)
            {
                Service.PluginLog.Info($"Arrived at flag, attempting dismount.");

                // Dismount
                var dismountResult = await DismountAsync(token);

                if (dismountResult)
                {
                    Service.PluginLog.Info("Dismounted.");
                }
                else
                {
                    Service.PluginLog.Error("Failed to dismount.");
                }

                return dismountResult;
            }
        }
        catch (OperationCanceledException)
        {
            Service.PluginLog.Info("Fly cancelled.");
        }
        catch (ObjectDisposedException)
        {
            Service.PluginLog.Info("Fly cancelled.");
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error($"Fly: {ex}");
        }

        return false;
    }

    // --------------------- Helpers --------------------- /

    private void InitializeRelicEventItem()
    {
        var excelItems = Service.DataManager.GetExcelSheet<EventItem>();
    
        foreach (var i in Service.GameInventory.GetInventoryItems(GameInventoryType.KeyItems))
        {
            var excelItem = excelItems
                .Where(item => _listOfBooks.Contains(item.RowId))
                .FirstOrNull(item => item.RowId == i.BaseItemId);

            if (!excelItem.HasValue)
                continue;

            this._atmaWindow.Target = null;
            this._atmaWindow.Completion = 0;
            this._atmaWindow.Total = 0;
            RelicBookGameItem = i;
            Service.PluginLog.Info($"Relic: { excelItem.Value.Name.ToString()} (Loaded)");
            _currentBook = BraveBook.GetValue((uint)(1 + _listOfBooks.IndexOf(excelItem.Value.RowId)));
            break;
        }
    }
    
    private static (int selectedCategoryIndex, int selectedNodeIndex) GetTargets(
        AddonRelicNoteBook addon, 
        AddonReceiveEventArgs receiveEventArgs)
    {
        var targetNodesDict = new Dictionary<int, AddonRelicNoteBook.TargetNode[]>
        {
            [0] =
            [
                addon.Enemy0,
                addon.Enemy1,
                addon.Enemy2,
                addon.Enemy3,
                addon.Enemy4,
                addon.Enemy5,
                addon.Enemy6,
                addon.Enemy7,
                addon.Enemy8,
                addon.Enemy9
            ],
            [1] =
            [
                addon.Dungeon0,
                addon.Dungeon1,
                addon.Dungeon2
            ],
            [2] =
            [
                addon.Fate0,
                addon.Fate1,
                addon.Fate2
            ],
            [3] =
            [
                addon.Leve0,
                addon.Leve1,
                addon.Leve2
            ]
        };
        
        for (var selectedCategoryIndex = 0; selectedCategoryIndex < 4; selectedCategoryIndex++)
        {
            var targetNodes = targetNodesDict[selectedCategoryIndex];
            for (var selectedNodeIndex = 0; selectedNodeIndex < targetNodes.Length; selectedNodeIndex++)
            {
                var targetNode = targetNodes[selectedNodeIndex];
                unsafe
                {
                    if (targetNode.CheckBox->AtkComponentButton.OwnerNode ==
                        receiveEventArgs.AtkEvent.As<AtkEvent>()->Target)
                    {
                        return (selectedCategoryIndex, selectedNodeIndex);
                    }
                }
            }
        }
        return (-1, -1);
    }
    
    private static BraveTarget[] GetContainer(BraveBook braveBook,
        int index)
    {
        return new Dictionary<int, BraveTarget[]>
        {
            [0] = braveBook.Enemies,
            [1] = braveBook.Dungeons,
            [2] = braveBook.Fates,
            [3] = braveBook.Leves
        }.GetValueOrDefault(index, []);
    }

    private List<Item> FindRelicItems()
    {
        return Service.GameInventory.GetInventoryItems(GameInventoryType.EquippedItems).ToArray()
            .Where(item => _listOfRelics.Contains(item.ItemId))
            .SelectMulti(item => Item.Get(item.ItemId))
            .ToList();
    }
    
    private static unsafe void GetRelicNoteBookVariables(AddonReceiveEventArgs receiveEventArgs,
        out BraveBook braveBook, out BraveTarget braveTarget, out byte completion, out byte total)
    {
        var addon = receiveEventArgs.Addon.Address.As<AddonRelicNoteBook>();
        var targets = GetTargets(*addon, receiveEventArgs);
        var relicNote = RelicNote.Instance();
        braveBook = BraveBook.GetValue(relicNote->RelicNoteId);
        braveTarget = GetContainer(braveBook, targets.selectedCategoryIndex)[targets.selectedNodeIndex];
        completion = targets.selectedCategoryIndex switch
        {
            0 => relicNote->GetMonsterProgress(targets.selectedNodeIndex),
            1 => Convert.ToByte(relicNote->IsDungeonComplete(targets.selectedNodeIndex)),
            2 => Convert.ToByte(relicNote->IsFateComplete(targets.selectedNodeIndex)),
            3 => Convert.ToByte(relicNote->IsLeveComplete(targets.selectedNodeIndex)),
            _ => 0
        };
        total = Convert.ToByte(targets.selectedCategoryIndex == 0 ? 3 : 1);
    }
    
    private static (float AetherstreamX, float AetherstreamY) GetAetherstreamCoords(MapLinkPayload payload)
    {
        return ((payload.RawX / 1000f + payload.Map.Value.OffsetX) * payload.Map.Value.SizeFactor / 100f, 
            (payload.RawY / 1000f + payload.Map.Value.OffsetY) * payload.Map.Value.SizeFactor / 100f);
    }

    
    private static unsafe void FlagTargetOnMap(MapLinkPayload position)
    {
        // Flag the target on the map
        var coords = GetAetherstreamCoords(position);
        var agentMap = AgentMap.Instance();
        if (agentMap == null)
            return;
        
        agentMap->FlagMarkerCount = 0;
        agentMap->SetFlagMapMarker(position.TerritoryType.RowId, position.Map.RowId,
            coords.AetherstreamX, coords.AetherstreamY);
    }
    
    private static unsafe void TeleportToNearestAetheryte(MapLinkPayload mapLink)
    {
        var coords = GetAetherstreamCoords(mapLink);
        Telepo.Instance()->Teleport(Service.DataManager.GetExcelSheet<Aetheryte>()
            .Where(a => a.Territory.RowId == mapLink.TerritoryType.RowId)
            .Where(a => !a.Invisible)
            .OrderBy(a => Vector2.DistanceSquared(
                new Vector2(a.AetherstreamX - coords.AetherstreamX), 
                new Vector2(a.AetherstreamY - coords.AetherstreamY)
            ))
            .First().RowId, 0);
    }

    internal static bool IsFateRunning(BraveTarget braveTarget)
    {
        if (braveTarget.FateId == 0) return false;
        var isActive = Service.FateTable
            .Where(f => f.State is FateState.Running or FateState.Preparing or FateState.Ending)
            .Where(f => f.TerritoryType.RowId == braveTarget.Position.TerritoryType.RowId)
            .FirstOrDefault(f => f.FateId == braveTarget.FateId);

        return isActive != null;
    }

    private static async Task<bool> WaitForConditions(Func<bool> condition, bool state, CancellationToken token)
    {
        return await Svc.Framework.RunOnTick(async () =>
        {
            var sw = Stopwatch.StartNew();
            while (condition() != state && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(200, token);
            }

            return condition() == state;
        }, cancellationToken: token);
    }
    
    private static Task WaitForMountAsync(CancellationToken token)
    {
        return Svc.Framework.RunOnTick(async () =>
        {
            while (Mount && !Svc.Condition[ConditionFlag.Mounted])
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(200, token);
            }
        }, cancellationToken: token);
    }

    internal async Task<bool> MoveToAsync(Vector3 target, CancellationToken token, bool fly = true)
    {
        const float distThreshold = 3f;
        var pollDelay = TimeSpan.FromMilliseconds(100);
        var requestedMove = false;
        var timeout = TimeSpan.FromMinutes(2);
        var sw = Stopwatch.StartNew();

        while (!token.IsCancellationRequested && sw.Elapsed < timeout)
        {
            // Read distance on main thread (matches your existing approach)
            var dist = await Svc.Framework.RunOnTick(() =>
                Vector3.Distance(Svc.ClientState.LocalPlayer.Position, target), cancellationToken: token);

            // Success: close enough and navmesh is idle
            if (dist <= distThreshold)
                return true;
            
            // Request movement once when navmesh is idle
            if (!VNavmesh.Path.IsRunning() && !VNavmesh.SimpleMove.PathfindInProgress())
            {
                if (requestedMove)
                {
                    if (Cts != null)
                    {
                        CancelPathfinding();
                    }
                }
                else
                {
                    await Svc.Framework.RunOnTick(() =>
                        VNavmesh.SimpleMove.PathfindAndMoveTo(target, fly), cancellationToken: token);
                    requestedMove = true;
                }
            }
            else if (fly && !Svc.Condition[ConditionFlag.Mounted] && Cts != null)
            {
                CancelPathfinding();
            }

            await Task.Delay(pollDelay, token);
        }

        return false;
    }

    private static unsafe bool Mount
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
    private static unsafe void Dismount()
    {
        if (!Svc.Condition[ConditionFlag.Mounted]) return;
        var am = ActionManager.Instance();
        if (am->GetActionStatus(ActionType.Mount, 0) == 0)
            am->UseAction(ActionType.Mount, 0);
    }

    private static async Task<bool> DismountAsync(CancellationToken token)
    {
        const int dismountDelayMs = 420;
        const int pathfindingDelayMs = 67;
        const int timeoutMs = 6969;
        const int retryAttempts = 5;
        var sw = Stopwatch.StartNew();
        
        for (var i = 0 ; i < retryAttempts ; i++)
        {
            // Repeat dismount
            while (Svc.Condition[ConditionFlag.Mounted] && sw.ElapsedMilliseconds < timeoutMs)
            {
                token.ThrowIfCancellationRequested();
                await Svc.Framework.RunOnTick(Dismount, cancellationToken: token);
                await Task.Delay(dismountDelayMs, token);
            }

            if (!Svc.Condition[ConditionFlag.Mounted])
                return true;

            if (Svc.Condition[ConditionFlag.InFlight])
            {
                Service.PluginLog.Warning($"Trying to recover after failing to dismount.");

                var player = await Svc.Framework.RunOnTick(() => Svc.ClientState.LocalPlayer, cancellationToken: token);

                var landing = await Svc.Framework.RunOnTick(() =>
                {
                    try
                    {
                        return VNavmesh.Query.Mesh.PointOnFloor(player.Position, false, 25f);
                    }
                    catch (Exception)
                    {
                        Service.ChatGui.PrintError($"[ZodiacBuddy] Could not land. Will retry {retryAttempts - i - 1 } times.");
                        Service.PluginLog.Error($"Could not land. Will retry {retryAttempts - i - 1 } times.");
                        return Vector3.Zero;
                    }
                }, cancellationToken: token);

                if (landing == Vector3.Zero)
                    continue;

                await Svc.Framework.RunOnTick(() =>
                {
                    VNavmesh.SimpleMove.PathfindAndMoveTo(landing, true);
                }, cancellationToken: token);

                while (VNavmesh.SimpleMove.PathfindInProgress() || 
                       VNavmesh.Path.IsRunning()) 
                    await Task.Delay(pathfindingDelayMs, token);
            }

            Service.PluginLog.Info($"End of loop, {retryAttempts - i - 1 } more attempts remaining.");
        }

        return false;
    }

    private async void CancelPathfinding()
    {
        try
        {
            if (Cts is { IsCancellationRequested: false }) await Cts.CancelAsync();
            Cts?.Dispose();
            Cts = null;
            if (VNavmesh.Path.IsRunning()) VNavmesh.Path.Stop();
            if (VNavmesh.Nav.PathfindInProgress()) VNavmesh.Nav.PathfindCancelAll();
        }
        catch (Exception e)
        {
            Service.PluginLog.Warning($"Failed to cancel pathfinding: {e}");
        }
    }

    private unsafe void DoesEquippedContainExpectedRelic(IFramework framework)
    {
        try
        {
            var instance = RelicNote.Instance();
            if (instance == null) return;
            var expectedId = instance->RelicId;
            if (expectedId == 0) return;
            if (_stopwatch.Elapsed < TimeSpan.FromSeconds(1)) return;
            _stopwatch.Restart();

            _usingCorrectRelic = Service.GameInventory.GetInventoryItems(GameInventoryType.EquippedItems).ToArray()
                .Any(item => _listOfRelics.ElementAt(expectedId - 1) == item.ItemId);
            if (_usingCorrectRelic) return;
            _atmaWindow.Target = null;
            _atmaWindow.Completion = 0;
            _atmaWindow.Total = 0;
        }
        catch (Exception)
        {
            // Ignored
        }
    }
    
    [GeneratedRegex(@"^Record of FATE completion added for .*$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-GB")]
    private static partial Regex FateCompletionRegex();
    
    [GeneratedRegex(@"^Record of leve completion added for .*$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-GB")]
    private static partial Regex LeveCompletionRegex();
    
    [GeneratedRegex(@"^Record of (.+?) kill \((\d+\/\d+)\) added for .*$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-GB")]
    private static partial Regex EnemyKillRegex();

    [GeneratedRegex(@"^You have obtained a book from the Trials of the Braves\. The objectives therein can be verified by using the item in the Key Items menu\.$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-GB")]
    private static partial Regex BookAddedRegex();

    [GeneratedRegex(@"^You throw away a book from the Trials of the Braves\.$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-GB")]
    private static partial Regex BookRemovedRegex();
}