using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using System.Numerics;
using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Inventory;
using ECommons;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using ZodiacBuddy.SmartCaseUtil;

namespace ZodiacBuddy.Stages.Atma;

internal class AtmaWindow : Window
{
    public BraveTarget? Target;
    public byte Completion;
    public byte Total;
    private Stopwatch _pathingTimer = new();
    
    public AtmaWindow() : base("Atma Window", ImGuiWindowFlags.AlwaysAutoResize)
    { 
        IsOpen = Service.Configuration.IsAtmaWindowOpen;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(225, 75),
        };
    }
    
    // Main Methods
    
    public override void OnOpen()
    {
        Service.Configuration.IsAtmaWindowOpen = true;
        Service.Configuration.Save();
    }
    
    public override void OnClose()
    {
        Service.Configuration.IsAtmaWindowOpen = false;
        Service.Configuration.Save();
    }
    
    /*
     Read-only checkbox that draws:
     - Checked: green tick
     - Unchecked: red cross
     - Optional: yellow '?' indicator (non-interactive by default).
     */
    private static void TickCrossCheckbox(string label, bool state, bool useQuestionMark = false, string errorText = "")
    {
        var style = ImGui.GetStyle();
        var boxSize = ImGui.GetFrameHeight();

        var cursor = ImGui.GetCursorScreenPos();
        var boxMax = cursor + new Vector2(boxSize, boxSize);

        var labelSize = ImGui.CalcTextSize(label);

        var totalWidth =
            boxSize + style.ItemInnerSpacing.X + labelSize.X +
            (useQuestionMark ? (style.ItemInnerSpacing.X + boxSize * 0.55f) : 0f);

        ImGui.Dummy(new Vector2(totalWidth, boxSize));

        var dl = ImGui.GetWindowDrawList();

        var borderCol = new Vector4(0.2f, 0.2f, 0.2f, 1f);
        var bgCol = new Vector4(0.15f, 0.15f, 0.15f, 1f);

        var borderU32 = ImGui.GetColorU32(borderCol);
        var bgU32 = ImGui.GetColorU32(bgCol);

        var rounding = style.FrameRounding;
        dl.AddRectFilled(cursor, boxMax, bgU32, rounding);
        dl.AddRect(cursor, boxMax, borderU32, rounding);
        
        if (state)
        {
            var tickGreen = new Vector4(0f, 1f, 0f, 1f);
            var colU32 = ImGui.GetColorU32(tickGreen);

            const float thickness = 2.5f;

            var p1 = new Vector2(cursor.X + boxSize * 0.25f, cursor.Y + boxSize * 0.55f);
            var p2 = new Vector2(cursor.X + boxSize * 0.42f, cursor.Y + boxSize * 0.72f);
            var p3 = new Vector2(cursor.X + boxSize * 0.78f, cursor.Y + boxSize * 0.30f);

            dl.AddLine(p1, p2, colU32, thickness);
            dl.AddLine(p2, p3, colU32, thickness);
        }
        else if (useQuestionMark)
        {
            var qmCol = new Vector4(1f, 0.9f, 0.2f, 1f);
            var qmU32 = ImGui.GetColorU32(qmCol);

            var r = boxSize * 0.33f;
            var center = new Vector2(cursor.X + boxSize * 0.5f, cursor.Y + boxSize * 0.5f);

            dl.AddCircleFilled(center, r, qmU32);

            var qmTextCol = new Vector4(0.1f, 0.1f, 0.1f, 1f);
            var qmTextU32 = ImGui.GetColorU32(qmTextCol);

            const string qm = "?";
            var qmTextSize = ImGui.CalcTextSize(qm);

            var qmTextPos = new Vector2(
                center.X - qmTextSize.X * 0.5f,
                center.Y - qmTextSize.Y * 0.5f
            );

            dl.AddText(qmTextPos, qmTextU32, qm);
            if (string.IsNullOrEmpty(errorText)) return;
            ImGui.SetCursorScreenPos(cursor);
            ImGui.InvisibleButton($"##hover_qm_{label}", new Vector2(boxSize, boxSize));

            if (ImGui.IsItemHovered()) ImGui.SetTooltip(errorText);
        }
        else
        {
            var crossRed = new Vector4(1f, 0f, 0f, 1f);
            var colU32 = ImGui.GetColorU32(crossRed);

            const float thickness = 2.5f;

            var p1 = new Vector2(cursor.X + boxSize * 0.25f, cursor.Y + boxSize * 0.25f);
            var p2 = new Vector2(cursor.X + boxSize * 0.75f, cursor.Y + boxSize * 0.75f);
            var p3 = new Vector2(cursor.X + boxSize * 0.75f, cursor.Y + boxSize * 0.25f);
            var p4 = new Vector2(cursor.X + boxSize * 0.25f, cursor.Y + boxSize * 0.75f);

            dl.AddLine(p1, p2, colU32, thickness);
            dl.AddLine(p3, p4, colU32, thickness);
        }

        var textPos = new Vector2(
            cursor.X + boxSize + style.ItemInnerSpacing.X,
            cursor.Y + (boxSize - labelSize.Y) * 0.5f
        );

        unsafe
        {
            dl.AddText(textPos, ImGui.GetColorU32(*ImGui.GetStyleColorVec4(ImGuiCol.Text)), 
                label);
        }

        if (string.IsNullOrEmpty(errorText)) return;
        ImGui.SetCursorScreenPos(cursor);
        ImGui.InvisibleButton($"##hover_qm_{label}", new Vector2(boxSize, boxSize));

        if (ImGui.IsItemHovered()) ImGui.SetTooltip(errorText);
    }

    public override void Draw()
    {
        if (!DependenciesMet()) return;
        RenderRelicBookButton();
        
        if (!ImGui.BeginTable("gridTable", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit)) return;

        ImGui.TableSetupColumn("C1", ImGuiTableColumnFlags.WidthFixed, 75f);
        ImGui.TableSetupColumn("C2", ImGuiTableColumnFlags.WidthStretch);
        
        // ---------------- Row 2 ----------------
        ImGui.TableNextRow();
        RenderTargetStatus();
        if (!VNavmesh.Nav.IsReady() || VNavmesh.Path.IsRunning() || VNavmesh.Nav.PathfindInProgress())
        {
            ImGui.TableNextRow();
            RenderVNavMesh();
        }
        if (AutoDuty.Enabled && AutoDuty.IsNavigating())
        {
            ImGui.TableNextRow();
            RenderAutoDuty();
        }
        ImGui.EndTable();
    }

    private void RenderTargetStatus()
    {
        var normalColourVector = Completion switch
        {
            1 => new Vector4(0f, 1f, 0f, 1f),
            _ => new Vector4(1f, 0f, 0f, 1f),
        };

        var enemyColourVector = Completion switch
        {
            1 or 2 => new Vector4(1f, 1f, 0f, 1f),
            3 => new Vector4(0f, 1f, 0f, 1f),
            _ => new Vector4(1f, 0f, 0f, 1f),
        };
        
        ImGui.TableSetColumnIndex(0);
        ImGui.Text("Target:");
        ImGui.TableNextColumn();
        ImGui.TextColored(new Vector4(1f, 0.6470588f, 0.0f, 1.0f), 
            SmartCaseHelper.SmartTitleCase(Target?.Name ?? "None"));
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (Target.HasValue)
        {
            switch (Target)
            {
                case { Issuer.Length: > 0 }:
                    ImGui.Text("Issuer:");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(new Vector4(1f, 1f, 0f, 1f),
                        SmartCaseHelper.SmartTitleCase(Target.Value.Issuer));
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    break;
                case { FateId: > 0 } when Completion == 0:
                    ImGui.Text("Status:");
                    ImGui.TableNextColumn();
                    ImGui.BeginTable("openBookChild", 2,
                        ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoHostExtendX);
                    ImGui.TableSetupColumn("x1", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("x2", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    var isFateUp = AtmaManager.IsFateRunning(Target.Value);
                    var fateText = isFateUp ? "Up" : "Not Up";
                    ImGui.TextColored(isFateUp ? new Vector4(0f, 1f, 0f, 1f) : new Vector4(1f, 0f, 0f, 1f),
                        SmartCaseHelper.SmartTitleCase(fateText));
                    ImGui.TableNextColumn();
                    if (ImGui.Button("Travel", new Vector2(75f, 20f)) && !Svc.Condition[ConditionFlag.InCombat])
                    {
                        Service.AtmaManager.MarkFlagAndFly(Target.Value);
                    }
                    ImGui.EndTable();
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    break;
            }
        }
        ImGui.Text("Complete:");
        ImGui.TableNextColumn();
        if (Target is { ContentsFinderConditionId: 0, FateId: 0 } && Target.Value.Issuer.IsNullOrEmpty())
        {
            ImGui.TextColored(enemyColourVector, $"{Completion.ToString()} / {Total.ToString()}");
        }
        else
        {
            ImGui.TextColored(normalColourVector, $"{(Completion == Total && Total > 0 ? "Yes" : "No")}");
        }
    }

    // Helpers

    private static bool DependenciesMet()
    {
        var dependencies = new Dictionary<string, (bool enabled, bool required, string desc)>
        {
            { "VNavmesh", (VNavmesh.Enabled, true, "Pathing to content") },
            { "AutoDuty", (AutoDuty.Enabled, false, "Clear dungeons") }
        };
        
        if (dependencies.All(kv => !kv.Value.required || kv.Value.required == kv.Value.enabled)) return true;
        ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "Dependencies not met.");
        foreach (var (key, (enabled, required, desc)) in dependencies)
        {
            TickCrossCheckbox(key, enabled, !required, $"{(required ? "Required" : "Optional")} => {desc}");
        }
        return false;
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
    
    private void RenderRelicBookButton()
    {
        if (Service.AtmaManager.RelicBookGameItem.HasValue)
        {
            if (ImGui.Button("Open Book", new Vector2(Math.Max(SizeConstraints?.MinimumSize.X ?? 225, ImGui.GetWindowContentRegionMax().X - 9), 35)))
            {
                UseItem(Service.AtmaManager.RelicBookGameItem!.Value);
            }
        }
        else
        {
            ImGui.TextDisabled("Relic Book not found");
        }
    }

    private static void RenderVNavMesh()
    {
        ImGui.TableSetColumnIndex(0);
        ImGui.Text("VNavmesh:");
        ImGui.TableNextColumn();
        if (!VNavmesh.Nav.IsReady())
        {
            ImGui.TextColored(new Vector4(1f, 0f, 0f, 1f), "Not Ready");
        }
        else if (VNavmesh.Nav.PathfindInProgress())
        {
            ImGui.TextColored(new Vector4(1f, 1f, 0f, 1f), "Generating Path");
        }
        else if (VNavmesh.Path.IsRunning())
        {
            ImGui.TextColored(new Vector4(0f, 1f, 0f, 1f), "Pathing");
        }
    }
    
    private static void RenderAutoDuty()
    {
        ImGui.TableSetColumnIndex(0);
        ImGui.Text("AutoDuty:");
        ImGui.TableNextColumn();
        if (AutoDuty.IsNavigating())
        {
            ImGui.TextColored(new Vector4(1f, 1f, 0f, 1f), "Navigating");
        }
    }
    
    internal async void FindNearest(IFramework framework)
    {
        try
        {
            unsafe
            {
                var addon = RelicNote.Instance();
                if (addon != null)
                {
                    // Service.PluginLog.Debug($"Relic Id: {addon->RelicId}, RelicNoteId: {addon->RelicNoteId}");
                }
            }
            
            // The goals must still exist
            if (Target == null || Completion >= Total) return;

            var autoDutyChecks = AutoDuty.Enabled && (AutoDuty.IsNavigating() || VNavmesh.Path.IsRunning() ||
                                                        VNavmesh.Nav.PathfindInProgress());
            var conditionChecks = Svc.Condition[ConditionFlag.InCombat] || Svc.Condition[ConditionFlag.InFlight] ||
                                  Svc.Condition[ConditionFlag.BeingMoved];
            
            // The character must not be in combat, in flight, or being moved
            if (autoDutyChecks || conditionChecks || Service.AtmaManager.Cts != null) return;
            
            var position = await Svc.Framework.RunOnTick(() => Player.Position);
            var nearest = Service.ObjectTable.CharacterManagerObjects
                .Where(playerObject => string.Equals(playerObject.Name.TextValue, Target!.Value.Name, StringComparison.CurrentCultureIgnoreCase))
                .Where(playerObject => playerObject.ObjectKind == ObjectKind.BattleNpc)
                .Where(playerObject => !playerObject.IsDead)
                .OrderBy(playerObject => Vector3.Distance(position, playerObject.Position))
                .FirstOrDefault();
            
            if (!_pathingTimer.IsRunning) _pathingTimer = Stopwatch.StartNew();

            switch (_pathingTimer.IsRunning)
            {
                case true when nearest == null
                               && Target.Value is { ContentsFinderConditionId: 0, FateId: 0 }
                               && Target.Value.Issuer.IsNullOrEmpty()
                               && _pathingTimer.Elapsed > TimeSpan.FromSeconds(5)
                               && Vector3.Distance(position, VNavmesh.Query.Mesh.FlagToPoint()) < 150:
                {
                    _pathingTimer.Reset();
                    Service.PluginLog.Debug("No targets found.");
                    Service.AtmaManager.Cts = new CancellationTokenSource();
                    await System.Threading.Tasks.Task.Run(() => Service.AtmaManager.MoveToAsync(VNavmesh.Query.Mesh.FlagToPoint(), Service.AtmaManager.Cts.Token, false));
                    Service.AtmaManager.Cts = null;
                }
                    return;
                case true when nearest != null && _pathingTimer.Elapsed > TimeSpan.FromSeconds(2):
                {
                    _pathingTimer.Reset();
                    Service.PluginLog.Debug($"Found nearest target... {nearest}");
                    Service.AtmaManager.Cts = new CancellationTokenSource();
                    await System.Threading.Tasks.Task.Run(() => Service.AtmaManager.MoveToAsync(nearest.Position, Service.AtmaManager.Cts.Token, false));
                    Service.AtmaManager.Cts = null;
                }
                    break;
            }
        }
        catch (Exception)
        {
            // ignored
        }
    }
}