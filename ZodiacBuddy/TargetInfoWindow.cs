using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Inventory;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Common.Math;
using Dalamud.Bindings.ImGui;
using System;
using Dalamud.Utility;
using ZodiacBuddy.Stages.Atma;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using ZodiacBuddy.SmartCaseUtil;
using ZodiacBuddy.Stages.Atma.Data;
using TaskManager = ECommons.Automation.LegacyTaskManager.TaskManager;

namespace ZodiacBuddy
{
    internal class TargetInfoWindow : Window
    {
        public string? CurrentTarget;
        public string? KillCount;
        public bool CompletedObjective => this.KillCount?.StartsWith('3') ?? false;
        public Vector3? CurrentTargetPosition { get; private set; }
        public GameInventoryItem? RelicBookGameItem { get; set; }

        // Ones that already existed
        private readonly TaskManager _taskManager = new();
        private ulong _currentTargetId;
        private bool _pendingPathing;
        private DateTime _lastPathingTime = DateTime.MinValue;
        private bool _rsrEnabled;

        public TargetInfoWindow() : base("ZodiacBuddy Target Info", ImGuiWindowFlags.AlwaysAutoResize)
        {
            this.IsOpen = Service.Configuration.TargetInfoWindowWasOpen;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(225, 75),
            };
            Svc.Framework.Update += OnFrameworkUpdate;
        }
        public override void OnOpen()
        {
            Service.Configuration.TargetInfoWindowWasOpen = true;
            Service.Configuration.Save();
        }
        public override void OnClose()
        {
            Service.Configuration.TargetInfoWindowWasOpen = false;
            Service.Configuration.Save();
        }
        public void Dispose()
        {
            Service.Configuration.TargetInfoWindowWasOpen = Service.Plugin.TargetWindow?.IsOpen ?? false;
            Service.Configuration.Save();
            Svc.Framework.Update -= OnFrameworkUpdate;
        }
        public enum TargetingState
        {
            Idle,
            AwaitingAtmaPathing,
            Active
        }
        public TargetingState State = TargetingState.Idle;
        private void OnFrameworkUpdate(IFramework framework)
        {
            if (!IPCSubscriber.IsReady("vnavmesh"))
                return;
            
            if (State != TargetingState.Active)
                return;
            
            if (!Svc.ClientState.IsLoggedIn || Svc.Condition[ConditionFlag.BetweenAreas]) return;

            if (CompletedObjective)
            {
                this._pendingPathing = false;
            
                if (State != TargetingState.AwaitingAtmaPathing)
                {
                    Service.PluginLog.Debug("Reached 3 kills. Locking logic and clearing target.");
                    State = TargetingState.AwaitingAtmaPathing;
            
                    this._currentTargetId = 0;
                    CurrentTargetPosition = null;
                    // TargetingHelper.StoredTargetId = 0;
                    // TargetingHelper.ResetAutoTargetFlag();
            
                    if (this._rsrEnabled)
                    {
                        Service.PluginLog.Debug("Kill complete � disabling RSR via /rotation off.");
                        Service.CommandManager.ProcessCommand("/rotation off");
                        this._rsrEnabled = false;
                    }
                }
            
                // Handle post-kill combat case
                if (Svc.Condition[ConditionFlag.InCombat])
                {
                    // TargetingHelper.PromoteAggroingEnemy();
                    this._pendingPathing = false;
            
                    if (!this._rsrEnabled)
                    {
                        this._taskManager.Enqueue(() =>
                        {
                            Service.CommandManager.ProcessCommand("/rotation manual");
                            this._rsrEnabled = true;
                            return true;
                        });
                    }
                }
                else if (this._rsrEnabled)
                {
                    Service.CommandManager.ProcessCommand("/rotation off");
                    this._rsrEnabled = false;
                    this._pendingPathing = false;
                }
            }
            else
            {
                this._pendingPathing = !Svc.Condition[ConditionFlag.InCombat];
                
                if (this._pendingPathing && !VNavmesh.Path.IsRunning() && (DateTime.Now - this._lastPathingTime).TotalSeconds > 2)
                {
                    StartPathingToCurrentTarget();

                    this._taskManager.Enqueue(() => AtmaManager.Dismount, "Dismount");
                    
                    UpdateCurrentTargetInfo();
                }
            }
        }
        
        public void SetTarget(string name, ulong id = 0)
        {
            // RegisteredKills.Clear();
            if (State == TargetingState.Active)
                return;
            
            CurrentTarget = SmartCaseHelper.SmartTitleCase(name);
            this._currentTargetId = id;
            CurrentTargetPosition = null;
            
            // if (!string.IsNullOrWhiteSpace(name))
            //     TargetingHelper.StartKillTracking(name);
            
            // if (id != 0)
            // {
            //     TargetingHelper.StoredTargetId = id;
            //     TargetingHelper.ResetAutoTargetFlag();
            // }
            
            State = TargetingState.AwaitingAtmaPathing;
        }

        // This is called by AtmaManager once /vnav moveflag finishes
        public void OnAtmaPathingComplete()
        {
            Service.PluginLog.Debug("Atma Pathing complete, unlocking targeting logic.");
            State = TargetingState.Active;
            this._pendingPathing = true;
            if (!this._rsrEnabled)
            {
                Service.PluginLog.Debug("Enabling RSR via /rotation manual.");
                this._taskManager.Enqueue(() =>
                {
                    Service.CommandManager.ProcessCommand("/rotation manual");
                    this._rsrEnabled = true;
                    return true;
                });
            }
        }

        private void StartPathingToCurrentTarget()
        {
            if (VNavmesh.Path.IsRunning())
            {
                this._pendingPathing = true;
                return;
            }
            if (CurrentTargetPosition != null)
            {
                var pos = CurrentTargetPosition.Value;
                if (!IPCSubscriber.IsReady("vnavmesh") || 
                    !VNavmesh.Nav.IsReady() || 
                    Svc.Condition[ConditionFlag.BetweenAreas])
                {
                    this._pendingPathing = true;
                    return;
                }

                VNavmesh.SimpleMove.PathfindAndMoveTo(pos, false);
            
                Service.ChatGui.Print($"Pathing to {CurrentTarget} at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})");
                this._lastPathingTime = DateTime.Now;
                this._pendingPathing = false;
            }
            else
            {
                if (CompletedObjective || Svc.Condition[ConditionFlag.InCombat])
                {
                    this._pendingPathing = false;
                    return;
                }
                var fallbackCommand = "/vnav moveflag";
                Service.PluginLog.Debug($"Issuing fallback pathing: {fallbackCommand}");
                Service.CommandManager.ProcessCommand(fallbackCommand);
                Service.ChatGui.Print("No enemy found nearby. Pathing to map flag.");
                AtmaManager.OnFallbackPathIssued?.Invoke();
                this._lastPathingTime = DateTime.Now;
                this._pendingPathing = false;
            }
        }

        public void UpdateCurrentTargetInfo()
        {
            if (!string.IsNullOrEmpty(CurrentTarget))
            {
                var previousId = this._currentTargetId;
            
                var playerPosition = Player.Object?.Position ?? Vector3.Zero;
                ICharacter? match = null;
                var bestDistance = float.MaxValue;
            
                foreach (var obj in Svc.Objects)
                {
                    if (obj.ObjectKind != ObjectKind.BattleNpc)
                        continue;
                    if (obj is not ICharacter c)
                        continue;
                    if (c.CurrentHp <= 0)
                        continue;
                    if (!obj.Name.TextValue.Equals(CurrentTarget, StringComparison.OrdinalIgnoreCase))
                        continue;
            
                    var distance = Vector3.Distance(c.Position, playerPosition);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        match = c;
                    }
                }
            
                if (match != null)
                {
                    if (!Svc.Condition[ConditionFlag.InCombat])
                    {
                        Service.TargetManager.Target = Service.ObjectTable.SearchById(match.GameObjectId);
                    }
                    
                    // Only switch if current ID is 0 or the current target is no longer valid
                    if (this._currentTargetId == 0)
                    {
                        this._currentTargetId = match.GameObjectId;
                        Service.PluginLog.Debug($"Set new target ID: {this._currentTargetId}");
                    }
                    else if (match.GameObjectId != this._currentTargetId)
                    {
                        ICharacter? previousTarget = null;
                        foreach (var obj in Svc.Objects)
                        {
                            if (obj is ICharacter character &&
                                character.ObjectKind == ObjectKind.BattleNpc &&
                                character.GameObjectId == this._currentTargetId)
                            {
                                previousTarget = character;
                                break;
                            }
                        }
            
                        if (previousTarget == null || previousTarget.CurrentHp == 0)
                        {
                            Service.PluginLog.Debug($"Previous target {this._currentTargetId} gone or dead. Checking for duplicate registration.");
            
                            // if (CurrentTargetId != 0 && !RegisteredKills.Contains(CurrentTargetId))
                            // {
                                // RegisteredKills.Add(CurrentTargetId);
                                // TargetingHelper.RegisterKillIfMatches(CurrentTargetId, CurrentTarget ?? "");
                            // }
                            // else
                            // {
                                // Service.PluginLog.Debug($"Skipping duplicate or zero-ID kill registration for {CurrentTargetId}.");
                            // }
                            this._currentTargetId = 0;
                            CurrentTargetPosition = null;
                            // TargetingHelper.StoredTargetId = 0;
                            // TargetingHelper.ResetAutoTargetFlag();
                        }
                        else
                        {
                            Service.PluginLog.Debug($"Previous target {this._currentTargetId} still alive. Not registering kill.");
                        }
                        Service.PluginLog.Debug($"Switching target from {previousId} to {match.GameObjectId}.");
                        this._currentTargetId = match.GameObjectId;
                        // TargetingHelper.StoredTargetId = match.GameObjectId;
                        // TargetingHelper.ResetAutoTargetFlag();
                    }
                    if (CurrentTargetPosition == null || Vector3.Distance(CurrentTargetPosition!.Value, match.Position) > 2f)
                    {
                        CurrentTargetPosition = match.Position;
                        StartPathingToCurrentTarget();
                    }
                    // TargetingHelper.AutoTargetStoredIdIfVisible();
                }
                else if (this._currentTargetId != 0 || CurrentTargetPosition != null)
                {
                    Service.PluginLog.Debug($"Lost sight of {CurrentTarget}, checking for kill...");
        
                    // if (this._currentTargetId != 0 && !RegisteredKills.Contains(this._currentTargetId))
                    // {
                    //     RegisteredKills.Add(this._currentTargetId);
                    //     TargetingHelper.RegisterKillIfMatches(this._currentTargetId, CurrentTarget ?? "");
                    // }
                    // else
                    // {
                    //     Service.PluginLog.Debug($"Skipping duplicate or zero-ID kill registration for {this._currentTargetId}.");
                    // }
        
                    this._currentTargetId = 0;
                    CurrentTargetPosition = null;
                    // TargetingHelper.StoredTargetId = 0;
                    // TargetingHelper.ResetAutoTargetFlag();
        
                    if (!CompletedObjective)
                        this._pendingPathing = true;
                }
                return;
            }
            
            var target = Svc.Targets.Target;
            if (target is { ObjectKind: ObjectKind.BattleNpc })
            {
                CurrentTarget = SmartCaseHelper.SmartTitleCase(target.Name.TextValue.Trim());
                this._currentTargetId = target.GameObjectId;
                CurrentTargetPosition = target.Position;
            }
        }

        public override void Draw()
        {
            var atmaEnabled = Service.Configuration.IsAtmaManagerEnabled;
            if (ImGui.Checkbox("Enable Atma Manager", ref atmaEnabled))
            {
                Service.Configuration.IsAtmaManagerEnabled = atmaEnabled;
                Service.Configuration.Save();
            }

            ImGui.Separator();

            if (ImGui.Button("Open Book", new Vector2(SizeConstraints?.MinimumSize.X ?? 100, 35)))
            {
                if (this.RelicBookGameItem.HasValue)
                {
                    UseItem(this.RelicBookGameItem.Value);
                }
            }
            
            ImGui.Separator();

            UpdateStatusUIOnly();
        }

        private static unsafe void UseItem(GameInventoryItem gameItem)
        {
            var agentModule = Framework.Instance()->GetUIModule()->GetAgentModule();
            if (agentModule == null)
                return;

            Service.Plugin.PrintMessage($"RowId: {gameItem.ItemId}, " +
                                        $"ContainerType: {gameItem.ContainerType}, " +
                                        $"Slot: {gameItem.InventorySlot}");
            
            agentModule->GetAgentInventoryContext()->UseItem(gameItem.ItemId, 
                (InventoryType) gameItem.ContainerType, gameItem.InventorySlot);
        }

        private void UpdateStatusUIOnly()
        {
            ImGui.Text(this.CurrentTarget.IsNullOrEmpty() ? "No target selected." : $"Target: {this.CurrentTarget}");

            if (this.KillCount.IsNullOrEmpty())
                return;

            if (CompletedObjective)
            {
                ImGui.TextColored(new Vector4(0f, 1f, 0f, 1f), "Kill Target Complete!");
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), $"Kills: {this.KillCount}");
            }
            
            ImGui.Separator();

            if (!VNavmesh.Nav.IsReady())
            {
                ImGui.TextColored(new Vector4(1f, 0f, 0f, 1f), "Status: Navmesh Not Ready");
            }
            else if (VNavmesh.Nav.PathfindInProgress())
            {
                ImGui.TextColored(new Vector4(1f, 1f, 0f, 1f), "Status: Generating Path...");
            }
            else if (VNavmesh.Path.IsRunning())
            {
                ImGui.TextColored(new Vector4(0f, 1f, 0f, 1f), "Status: Pathing");
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), "Status: Idle");
            }
        }

        public unsafe void SetTargetNode(AddonRelicNoteBook.TargetNode targetNode, BraveTarget enemy)
        {
            this.CurrentTarget = $"{SmartCaseHelper.SmartTitleCase(enemy.Name)}";
            if (enemy is { ContentsFinderConditionId: 0, FateId: 0 } && enemy.Issuer.IsNullOrEmpty())
                this.KillCount = $"{targetNode.CounterTextNode->GetText().ToString()}";
            else 
                this.KillCount = null;
        }
    }
}
