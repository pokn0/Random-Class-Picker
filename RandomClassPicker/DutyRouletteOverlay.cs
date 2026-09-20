using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;

namespace RandomClassPicker;

/// <summary>
/// 在游戏原生的"任务搜索器"窗口上叠加一层自绘界面：
/// 每个每日随机任务一个复选框（勾选的进入随机池），底部一个抽选按钮。
///
/// 实现方式：订阅 <see cref="IUiBuilder.Draw"/>，在任务搜索器可见时用 ImGui 按它的
/// 屏幕坐标画一个无背景窗口贴上去。这样不需要改动游戏的 ATK 节点，
/// 出问题时关掉开关即可，不影响游戏本体。
/// </summary>
public sealed class DutyRouletteOverlay : IDisposable
{
    private const string AddonName = "ContentsFinder";
    private const string WindowId = "##rcpDutyOverlay";
    private const string PanelTitle = "随机每日任务";

    private readonly Plugin plugin;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    private bool wasOpen;
    private Vector2 manualPos;
    private bool hasManualPos;

    public DutyRouletteOverlay(Plugin plugin, IGameGui gameGui, IPluginLog log)
    {
        this.plugin = plugin;
        this.gameGui = gameGui;
        this.log = log;

        Plugin.PluginInterface.UiBuilder.Draw += this.OnDraw;
    }

    /// <summary>最近一次抽中的随机任务名（用于界面外的提示）。</summary>
    public string? LastDrawnRoulette { get; private set; }

    private unsafe void OnDraw()
    {
        try
        {
            var config = this.plugin.Configuration;
            if (!config.ShowDutyFinderOverlay)
                return;

            var addon = this.gameGui.GetAddonByName(AddonName, 1);
            if (addon.IsNull)
            {
                this.wasOpen = false;
                return;
            }

            var unit = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon.Address;
            if (unit == null || !unit->IsVisible)
            {
                this.wasOpen = false;
                return;
            }

            // 刚打开时刷新一次完成状态（数据来自游戏界面同源的 API）
            if (!this.wasOpen)
            {
                this.wasOpen = true;
                this.plugin.DutyRoulettes.Refresh();
            }

            var scale = unit->Scale <= 0f ? 1f : unit->Scale;
            var pos = new Vector2(unit->X, unit->Y);

            // AtkUnitBase 上没有窗口尺寸字段，尺寸取自它的根节点
            var size = new Vector2(
                (unit->RootNode == null ? 0f : unit->RootNode->Width) * scale,
                (unit->RootNode == null ? 0f : unit->RootNode->Height) * scale);

            // 根节点还没算好尺寸时给个兜底，避免面板飞到屏幕外
            if (size.X < 100f || size.Y < 100f)
                size = new Vector2(760f, 560f);

            this.DrawPanel(pos, size);
        }
        catch (Exception ex)
        {
            // 叠加层出问题不能影响游戏，直接吞掉并记日志
            this.log.Debug(ex, "[RandomClassPicker] 绘制任务搜索器叠加层失败");
        }
    }

    private void DrawPanel(Vector2 addonPos, Vector2 addonSize)
    {
        var config = this.plugin.Configuration;
        var width = Math.Clamp(config.DutyFinderPanelWidth, 200f, 600f);
        var viewport = ImGui.GetMainViewport();
        var screen = viewport.Size;

        Vector2 panelPos;

        if (config.DutyFinderPanelManualPosition && this.hasManualPos)
        {
            // 用户自己拖过：用记住的位置，只做边界收敛
            panelPos = this.manualPos;
        }
        else if (config.DutyFinderPanelManualPosition)
        {
            // 第一次：默认贴在任务搜索器右侧
            panelPos = new Vector2(addonPos.X + addonSize.X - width, addonPos.Y + 60f);
        }
        else
        {
            // 自动模式：贴在任务搜索器**左侧**，避免和右侧的详情栏重叠
            panelPos = new Vector2(addonPos.X - width - 8f, addonPos.Y + 60f);
        }

        // 无论如何都不让面板跑出屏幕（之前就是这里算漏了，导致面板被切掉）
        var maxX = Math.Max(0f, screen.X - width - 4f);
        var maxY = Math.Max(0f, screen.Y - 120f);
        panelPos.X = Math.Clamp(panelPos.X, 4f, maxX);
        panelPos.Y = Math.Clamp(panelPos.Y, 4f, maxY);

        ImGui.SetNextWindowPos(panelPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, 0f), ImGuiCond.Always);

        var flags = ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav;

        // 自动模式不允许拖动（位置跟着窗口走）；手动模式允许拖动并记住
        if (!config.DutyFinderPanelManualPosition)
            flags |= ImGuiWindowFlags.NoMove;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 8f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.06f, 0.06f, 0.06f, 0.88f));

        // 手动摆放时给个标题栏便于拖动，自动模式不需要
        var opened = config.DutyFinderPanelManualPosition
            ? ImGui.Begin($"{PanelTitle}{WindowId}", flags)
            : ImGui.Begin(WindowId, flags);

        if (opened)
        {
            this.DrawContent();
        }

        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();

        // 记录用户拖动后的位置
        if (config.DutyFinderPanelManualPosition)
        {
            var actual = ImGui.GetWindowPos();
            this.manualPos = actual;
            this.hasManualPos = true;
        }
    }

    private void DrawContent()
    {
        var config = this.plugin.Configuration;
        var entries = this.plugin.DutyRoulettes.Entries;

        ImGui.Separator();

        if (entries.Count == 0)
        {
            ImGui.TextDisabled("未读到随机任务列表。");
        }
        else
        {
            foreach (var e in entries)
            {
                // 已完成的任务：显示为"已取消勾选"且不可操作。
                // 这里只是**显示层**的自动取消——保存的用户偏好不变，
                // 所以第二天重置后会自动重新勾上。
                var isCompleted = e.IsCompleted;
                var selected = !isCompleted && config.IsRouletteSelected(e.Id);

                if (isCompleted)
                    ImGui.TextDisabled("○");
                else
                    ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "●");

                ImGui.SameLine();

                ImGui.BeginDisabled(isCompleted);
                if (ImGui.Checkbox($"{ShortName(e.Name)}##rl{e.Id}", ref selected) && !isCompleted)
                {
                    config.SetRouletteSelected(e.Id, selected);
                    this.plugin.SaveConfig();
                    this.plugin.DtrBarHandler.Refresh();
                }

                ImGui.EndDisabled();

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        $"{(isCompleted ? "已完成（今日不再进入随机池）" : "未完成")}\n" +
                        $"随机任务 ID {e.Id}\n" +
                        $"完整名称: {e.Name}");
                }
            }
        }

        ImGui.Separator();

        var selectedCount = entries.Count(e => config.IsRouletteSelected(e.Id) && !e.IsCompleted);

        if (selectedCount == 0)
            ImGui.TextDisabled("请勾选至少一个未完成的随机任务。");

        ImGui.BeginDisabled(this.plugin.Window.IsBusy || selectedCount == 0 || !Plugin.ClientState.IsLoggedIn);

        if (ImGui.Button($"🎲 随机抽一个并排队 ({selectedCount})", new Vector2(-1, 32f)))
            this.plugin.RollDailyRoulette();

        ImGui.EndDisabled();

        if (ImGui.Button("全选", new Vector2(70f, 0f)))
        {
            this.plugin.ResetRouletteSelection();
        }

        ImGui.SameLine();
        if (ImGui.Button("全不选", new Vector2(70f, 0f)))
        {
            this.plugin.Configuration.SelectedRouletteIds = [];
            this.plugin.SaveConfig();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("仅未完成的会入池");

        // 排队中 / 队列状态提示
        var queueState = this.plugin.CurrentQueueDescription();
        if (!string.IsNullOrEmpty(queueState))
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), queueState);

        if (this.LastDrawnRoulette is { } last)
            ImGui.TextDisabled($"上次抽中: {last}");

        if (Plugin.Condition[ConditionFlag.InCombat])
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), "战斗中无法排队。");
    }

    /// <summary>
    /// 面板上只显示冒号后面的部分（"随机任务：练级迷宫" → "练级迷宫"），省横向空间。
    /// 没有冒号就原样显示。
    /// </summary>
    private static string ShortName(string fullName)
    {
        var idx = fullName.IndexOf('：');
        if (idx < 0)
            idx = fullName.IndexOf(':');

        return idx >= 0 && idx + 1 < fullName.Length
            ? fullName[(idx + 1)..].Trim()
            : fullName;
    }

    public void Dispose()
    {
        try
        {
            Plugin.PluginInterface.UiBuilder.Draw -= this.OnDraw;
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[RandomClassPicker] 注销叠加层失败");
        }
    }

    internal void SetLastDrawn(string name) => this.LastDrawnRoulette = name;

    /// <summary>清掉记住的拖动位置，下次重新按默认位置摆放。</summary>
    internal void ResetManualPosition()
    {
        this.manualPos = default;
        this.hasManualPos = false;
    }
}
