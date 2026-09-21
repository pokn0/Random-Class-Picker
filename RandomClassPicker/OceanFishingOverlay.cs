using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace RandomClassPicker;

/// <summary>
/// 出海垂钓"申请航线"菜单上的叠加面板：
/// 勾选近海/远海 → 随机抽一条 → 直接提交申请。
///
/// 识别方式见 <see cref="OceanFishingMenu"/>：同一个 SelectString 窗口也被限定职业任务等使用，
/// 所以按菜单内容（首行"要乘坐哪条航线？"）判定。
/// </summary>
public sealed class OceanFishingOverlay : IDisposable
{
    private const string AddonName = "SelectString";
    private const string WindowId = "##rcpOceanOverlay";
    private const string PanelTitle = "出海垂钓 · 随机航线";

    private readonly Plugin plugin;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly OceanFishingMenu menu;

    private List<RouteOption> options = [];
    private string lastResult = string.Empty;
    private bool lastWasError;

    public OceanFishingOverlay(Plugin plugin, IGameGui gameGui, IPluginLog log)
    {
        this.plugin = plugin;
        this.gameGui = gameGui;
        this.log = log;
        this.menu = new OceanFishingMenu(gameGui, log);

        Plugin.PluginInterface.UiBuilder.Draw += this.OnDraw;
    }

    internal OceanFishingMenu Menu => this.menu;

    private unsafe void OnDraw()
    {
        try
        {
            var config = this.plugin.Configuration;
            if (!config.ShowOceanFishingOverlay)
                return;

            // 每帧重新读一次菜单内容：菜单项是动态的
            this.options = this.menu.ReadOptions();
            if (this.options.Count == 0)
                return;

            var addon = this.gameGui.GetAddonByName(AddonName, 1);
            if (addon.IsNull)
                return;

            var unit = (AtkUnitBase*)addon.Address;
            if (unit == null)
                return;

            var scale = unit->Scale <= 0f ? 1f : unit->Scale;
            var pos = new Vector2(unit->X, unit->Y);
            var size = new Vector2(
                (unit->RootNode == null ? 0f : unit->RootNode->Width) * scale,
                (unit->RootNode == null ? 0f : unit->RootNode->Height) * scale);

            this.DrawPanel(pos, size);
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[RandomClassPicker] 绘制出海垂钓叠加层失败");
        }
    }

    private void DrawPanel(Vector2 addonPos, Vector2 addonSize)
    {
        var config = this.plugin.Configuration;
        var width = Math.Clamp(config.OceanFishingPanelWidth, 200f, 600f);
        var screen = ImGui.GetMainViewport().Size;

        // 贴在申请航线窗口上方；放不下就贴左侧
        var panelPos = new Vector2(addonPos.X + addonSize.X - width, addonPos.Y - 8f);

        var estimatedHeight = 120f + (this.options.Count * 26f);
        if (panelPos.Y + estimatedHeight > screen.Y || panelPos.Y < 4f)
            panelPos = new Vector2(addonPos.X - width - 8f, addonPos.Y);

        // 边界收敛，绝不跑出屏幕
        panelPos.X = Math.Clamp(panelPos.X, 4f, Math.Max(4f, screen.X - width - 4f));
        panelPos.Y = Math.Clamp(panelPos.Y, 4f, Math.Max(4f, screen.Y - estimatedHeight));

        ImGui.SetNextWindowPos(panelPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, 0f), ImGuiCond.Always);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 8f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.06f, 0.06f, 0.06f, 0.9f));

        if (ImGui.Begin($"{PanelTitle}{WindowId}", flags))
            this.DrawContent();

        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void DrawContent()
    {
        var config = this.plugin.Configuration;

        ImGui.TextUnformatted("选择哪些航线参与随机：");
        ImGui.Separator();

        // 勾选框按菜单里实际出现的航线生成
        foreach (var opt in this.options)
        {
            var selected = config.IsOceanRouteSelected(opt.Index);
            if (ImGui.Checkbox($"{opt.Name}##or{opt.Index}", ref selected))
            {
                config.SetOceanRouteSelected(opt.Index, selected);
                this.plugin.SaveConfig();
            }
        }

        ImGui.Separator();

        var pool = this.options.Where(o => config.IsOceanRouteSelected(o.Index)).ToList();
        if (pool.Count == 0)
            ImGui.TextDisabled("请至少勾选一条航线。");

        ImGui.BeginDisabled(this.plugin.Window.IsBusy || pool.Count == 0);

        if (ImGui.Button($"🎲 随机抽一条并申请 ({pool.Count})", new Vector2(-1, 32f)))
            this.Roll(pool);

        ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(this.lastResult))
        {
            ImGui.TextColored(
                this.lastWasError ? new Vector4(1f, 0.5f, 0.4f, 1f) : new Vector4(0.5f, 1f, 0.5f, 1f),
                this.lastResult);
        }

        ImGui.TextDisabled("抽取来源与插件设置里的随机源一致");
    }

    /// <summary>抽取并提交。抽签在后台线程，提交派回主线程。</summary>
    private void Roll(List<RouteOption> pool)
    {
        if (this.plugin.Window.IsBusy)
            return;

        this.plugin.Window.IsBusy = true;
        var source = this.plugin.Configuration.Source;

        _ = Task.Run(() =>
        {
            try
            {
                var draw = this.plugin.RandomClient.Draw(1, pool.Count, source);
                var picked = pool[draw.Value - 1];
                var sourceTag = draw.IsTrueRandom ? "真随机·random.org" : "伪随机·本地兜底";

                var poolNames = string.Join("，", pool.Select(o => o.Name));

                // 提交必须在主线程
                var submitted = Plugin.Framework.RunOnTick(() => this.menu.Select(picked.Index)).GetAwaiter().GetResult();

                if (submitted)
                {
                    this.lastResult = $"已申请 {picked.Name}（{sourceTag}）";
                    this.lastWasError = false;
                    Plugin.ChatGui.Print(
                        $"[随机职业] 从{pool.Count}条航线（{poolNames}）中抽取到了{picked.Name}，已提交申请 —— {sourceTag}");
                }
                else
                {
                    this.lastResult = $"抽到 {picked.Name}，但提交失败";
                    this.lastWasError = true;
                    Plugin.ChatGui.PrintError($"[随机职业] 抽到 {picked.Name}，但提交申请失败。");
                }
            }
            catch (Exception ex)
            {
                this.lastResult = $"抽签失败: {ex.Message}";
                this.lastWasError = true;
                this.log.Error(ex, "[RandomClassPicker] 出海垂钓抽签失败");
                Plugin.ChatGui.PrintError($"[随机职业] 出海垂钓抽签失败: {ex.Message}");
            }
            finally
            {
                this.plugin.Window.IsBusy = false;
            }
        });
    }

    public void Dispose()
    {
        try
        {
            Plugin.PluginInterface.UiBuilder.Draw -= this.OnDraw;
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[RandomClassPicker] 注销出海垂钓叠加层失败");
        }
    }
}
