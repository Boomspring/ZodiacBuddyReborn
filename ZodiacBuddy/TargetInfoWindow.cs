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
using System.Linq;
using Dalamud.Utility;
using ZodiacBuddy.Stages.Atma;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using ZodiacBuddy.SmartCaseUtil;
using ZodiacBuddy.Stages.Atma.Data;
using TaskManager = ECommons.Automation.LegacyTaskManager.TaskManager;
using Lumina.Excel.Sheets;

namespace ZodiacBuddy
{
    internal class TargetInfoWindow : Window
    {
        public string? CurrentTarget;
        public string? KillCount;
        public bool CompletedObjective => this.KillCount?.StartsWith('3') ?? false;
        public Vector3? CurrentTargetPosition { get; private set; }
        public GameInventoryItem? RelicBookGameItem;

        public TargetInfoWindow() : base("ZodiacBuddy Target Info", ImGuiWindowFlags.AlwaysAutoResize)
        {
            this.IsOpen = Service.Configuration.TargetInfoWindowWasOpen;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(225, 75),
            };
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
        }
        
        public void SetTarget(string name, ulong id = 0)
        {
            CurrentTarget = SmartCaseHelper.SmartTitleCase(name);
            CurrentTargetPosition = null;
        }

        public override void Draw()
        {
            if (!Svc.ClientState.IsLoggedIn || Svc.Condition[ConditionFlag.BetweenAreas]) return;
            
            var atmaEnabled = Service.Configuration.IsAtmaManagerEnabled;
            if (ImGui.Checkbox("Enable Atma Manager", ref atmaEnabled))
            {
                Service.Configuration.IsAtmaManagerEnabled = atmaEnabled;
                Service.Configuration.Save();
            }

            var enabledOnlyRelicEquipped = Service.Configuration.EnableOnlyWhenRelicEquipped;
            if (ImGui.Checkbox("Enable When Relic Equipped", ref enabledOnlyRelicEquipped))
            {
                Service.Configuration.EnableOnlyWhenRelicEquipped = enabledOnlyRelicEquipped;
                Service.Configuration.Save();
            }

            ImGui.Separator();

            this.UpdateRelicButton();
            
            ImGui.Separator();

            UpdateStatusUIOnly();
        }

        private void UpdateRelicButton()
        {
            // Temporary Code
            if (this.RelicBookGameItem.HasValue)
            {
                if (ImGui.Button("Open Book", new Vector2(this.SizeConstraints?.MinimumSize.X ?? 100, 35)))
                {
                    UseItem(this.RelicBookGameItem.Value);
                }
            }
            else
            {
                ImGui.TextDisabled("No Relic Book Found");
            }
        }

        private static unsafe void UseItem(GameInventoryItem gameItem)
        {
            var agentModule = Framework.Instance()->GetUIModule()->GetAgentModule();
            if (agentModule == null)
                return;

            Service.PluginLog.Debug($"RowId: {gameItem.ItemId}, " +
                                    $"ContainerType: {gameItem.ContainerType}, " +
                                    $"Slot: {gameItem.InventorySlot}");
            
            agentModule->GetAgentInventoryContext()->UseItem(gameItem.ItemId, 
                (InventoryType) gameItem.ContainerType, gameItem.InventorySlot);
        }

        private void UpdateStatusUIOnly()
        {
            ImGui.Text(this.CurrentTarget.IsNullOrEmpty() ? "No target selected." : $"Target: {this.CurrentTarget}");

            if (!this.KillCount.IsNullOrEmpty())
            {
                if (CompletedObjective)
                {
                    ImGui.TextColored(new Vector4(0f, 1f, 0f, 1f), "Kill Target Complete!");
                }
                else
                {
                    ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), $"Kills: {this.KillCount}");
                }
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
