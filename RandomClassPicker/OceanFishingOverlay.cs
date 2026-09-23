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

    /// <summary>
    /// 刚提交过航线申请的时刻。用于限定"自动确认"只在紧随其后的确认框上生效，
    /// 避免误点其它地方的确认框。
    /// </summary>
    private DateTime routeSubmittedAt = DateTime.MinValue;

    private static readonly TimeSpan AutoConfirmWindow = TimeSpan.FromSeconds(15);

    private readonly OceanFishingProbe probe;

    public OceanFishingOverlay(Plugin plugin, IGameGui gameGui, IPluginLog log)
    {
        this.plugin = plugin;
        this.gameGui = gameGui;
        this.log = log;
        this.menu = new OceanFishingMenu(gameGui, log);
        this.probe = new OceanFishingProbe(gameGui, log);

        Plugin.PluginInterface.UiBuilder.Draw += this.OnDraw;
    }

    // ------------------------------------------------------------------
    // 触发方式诊断
    // ------------------------------------------------------------------

    private List<OceanFishingMenu.TriggerMethod> diagnosticQueue = [];
    private int diagnosticListIndex;
    private DateTime diagnosticNextAt = DateTime.MinValue;
    private DateTime diagnosticTriedAt = DateTime.MinValue;
    private string diagnosticCurrentMethod = string.Empty;
    private bool diagnosticAwaitingResult;

    /// <summary>每种触发方式观察多久（毫秒）。太短会漏掉延迟弹出的确认框。</summary>
    private const int DiagnosticObserveMs = 4000;

    /// <summary>
    /// 依次尝试各种"选中航线"的触发方式：每种只调一次，然后**耐心观察 4 秒**
    /// 看确认框是否出现、以及菜单是否还开着。用于找出哪种方式能真正触发参加。
    /// </summary>
    internal void RunTriggerDiagnostic()
    {
        // 用游戏自己的 EntryNames 读菜单项，索引即游戏菜单索引
        var options = this.menu.ReadOptionsFromEntries();
        if (options.Count == 0)
        {
            Plugin.ChatGui.PrintError("[随机职业] 请先打开申请航线菜单（且保持可见），再执行该指令。");
            return;
        }

        this.diagnosticListIndex = options[0].ListIndex;

        this.diagnosticQueue =
        [
            OceanFishingMenu.TriggerMethod.ListSelectDispatch,
            OceanFishingMenu.TriggerMethod.FireCallbackSingle,
            OceanFishingMenu.TriggerMethod.FireCallbackIndexOne,
            OceanFishingMenu.TriggerMethod.ListSelectOnly,
            OceanFishingMenu.TriggerMethod.FireCallbackMinusOne,
        ];

        this.probe.Start();
        this.probe.Trace($"菜单航线数={options.Count}，测试目标=\"{options[0].Name}\"（菜单索引 {options[0].ListIndex}）");
        this.probe.Trace("观察窗口 4 秒/方式");
        this.diagnosticNextAt = DateTime.UtcNow;
        this.diagnosticAwaitingResult = false;
    }

    private void TickTriggerDiagnostic()
    {
        if (this.diagnosticQueue.Count == 0 && !this.diagnosticAwaitingResult)
            return;

        var now = DateTime.UtcNow;

        // 结算上一次尝试的观察结果
        if (this.diagnosticAwaitingResult)
        {
            if (now < this.diagnosticNextAt)
            {
                // 观察期间：一旦确认框出现就立刻记录并点掉
                if (this.menu.IsConfirmOpen())
                {
                    this.probe.Trace($"  ★ {this.diagnosticCurrentMethod} 让确认框出现了！");
                    this.probe.FoundWorkingMethod = this.diagnosticCurrentMethod;
                    this.menu.ConfirmYesNo();
                    this.probe.Trace("  → 已自动点「是」");
                    this.diagnosticAwaitingResult = false;
                    this.diagnosticQueue.Clear();
                }

                return;
            }

            // 观察超时：记录结论
            var menuStillOpen = this.menu.IsOpen;
            var confirmOpen = this.menu.IsConfirmOpen();
            this.probe.Trace(
                $"  → {this.diagnosticCurrentMethod} 观察结束: 确认框={(confirmOpen ? "出现" : "未出现")}, " +
                $"菜单={(menuStillOpen ? "仍打开" : "已关闭")}");
            this.diagnosticAwaitingResult = false;
        }

        if (this.diagnosticQueue.Count == 0)
        {
            this.FinishTriggerDiagnostic();
            return;
        }

        if (now < this.diagnosticNextAt)
            return;

        // 开始下一次尝试
        var method = this.diagnosticQueue[0];
        this.diagnosticQueue.RemoveAt(0);
        this.diagnosticCurrentMethod = method.ToString();

        if (!this.menu.IsOpen)
        {
            this.probe.Trace($"  {method}: 菜单已关闭，停止测试");
            this.diagnosticQueue.Clear();
            this.FinishTriggerDiagnostic();
            return;
        }

        this.menu.Trigger(this.diagnosticListIndex, method, this.probe);
        this.diagnosticAwaitingResult = true;
        this.diagnosticTriedAt = now;
        this.diagnosticNextAt = now.AddMilliseconds(DiagnosticObserveMs);
    }

    private void FinishTriggerDiagnostic()
    {
        var verdict = this.probe.FoundWorkingMethod ?? "（无 —— 5 种方式都没能让确认框出现）";
        var dump = this.probe.BuildDump("依次尝试 5 种触发方式，每种观察 4 秒") + $"\n\n结论: {verdict}";

        this.plugin.WriteDiagnosticPublic("ocean-trigger-test.txt", dump);

        Plugin.ChatGui.Print($"[随机职业] 触发方式测试完成。结论: {verdict}");
        Plugin.ChatGui.Print($"[随机职业] 详细结果: " +
            Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "ocean-trigger-test.txt"));
    }

    internal OceanFishingMenu Menu => this.menu;

    private unsafe void OnDraw()
    {
        try
        {
            var config = this.plugin.Configuration;

            // 自动确认紧随其后的"要乘坐 XX 航线吗？"确认框。
            // 只在刚提交过申请的一小段时间内生效，且不影响其它确认框。
            this.TryAutoConfirm();

            // 触发方式诊断（/rcp ikdtest）
            this.TickTriggerDiagnostic();

            if (!config.ShowOceanFishingOverlay)
                return;

            // 每帧重新读一次菜单内容：菜单项是动态的。
            // 用 EntryNames（游戏自己的菜单项数组）而不是 AtkValues
            this.options = this.menu.ReadOptionsFromEntries();
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

        // 面板宽度不能超过申请航线窗口的宽度：不然"放到窗口上方"时会在水平方向压到窗口
        var width = Math.Clamp(
            Math.Min(config.OceanFishingPanelWidth, Math.Max(180f, addonSize.X)),
            180f, 600f);

        var screen = ImGui.GetMainViewport().Size;

        var estimatedHeight = 130f + (this.options.Count * 26f);

        // 候选位置按优先级排列，挑第一个不与申请航线窗口重叠的。
        // 注意：一定要用菜单的**实际**尺寸算重叠，估算高度会导致面板压在菜单上。
        Span<Vector2> candidates =
        [
            new Vector2(addonPos.X + addonSize.X + 8f, addonPos.Y),  // 右侧
            new Vector2(addonPos.X - width - 8f, addonPos.Y),        // 左侧
            new Vector2(addonPos.X, addonPos.Y - estimatedHeight - 8f),  // 上方
            new Vector2(addonPos.X, addonPos.Y + addonSize.Y + 8f),      // 下方
        ];

        var chosen = candidates[0];
        foreach (var c in candidates)
        {
            if (this.Fits(c, width, estimatedHeight, screen) &&
                !Overlaps(c, width, estimatedHeight, addonPos, addonSize))
            {
                chosen = c;
                break;
            }
        }

        // 四边都放不下时，退回"菜单正上方并贴屏幕边缘"，至少保证在屏内
        if (Overlaps(chosen, width, estimatedHeight, addonPos, addonSize))
            chosen = new Vector2(addonPos.X, Math.Max(4f, addonPos.Y - estimatedHeight - 8f));

        // 边界收敛，绝不跑出屏幕
        chosen.X = Math.Clamp(chosen.X, 4f, Math.Max(4f, screen.X - width - 4f));
        chosen.Y = Math.Clamp(chosen.Y, 4f, Math.Max(4f, screen.Y - estimatedHeight - 4f));

        ImGui.SetNextWindowPos(chosen, ImGuiCond.Always);
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

    /// <summary>
    /// 若刚提交过航线申请、且游戏弹出了确认框，则自动点「是」。
    /// 时效限制保证它不会去点别处的确认框。
    /// </summary>
    private void TryAutoConfirm()
    {
        if (this.routeSubmittedAt == DateTime.MinValue)
            return;

        if (DateTime.UtcNow - this.routeSubmittedAt > AutoConfirmWindow)
        {
            this.routeSubmittedAt = DateTime.MinValue;
            return;
        }

        if (!this.plugin.Configuration.AutoConfirmOceanRoute)
            return;

        if (!this.menu.IsConfirmOpen())
            return;

        if (this.menu.ConfirmYesNo())
        {
            this.routeSubmittedAt = DateTime.MinValue;
            this.lastResult += " → 已自动确认";
            Plugin.ChatGui.Print("[随机职业] 已自动确认航线申请（点「是」）。");
        }
    }

    /// <summary>面板是否完整落在屏幕内。</summary>
    private bool Fits(Vector2 pos, float width, float height, Vector2 screen)
        => pos.X >= 4f && pos.Y >= 4f
           && pos.X + width <= screen.X - 4f
           && pos.Y + height <= screen.Y - 4f;

    /// <summary>面板是否与申请航线窗口重叠。</summary>
    private static bool Overlaps(
        Vector2 pos, float width, float height,
        Vector2 addonPos, Vector2 addonSize)
    {
        const float margin = 4f;
        var panelRight = pos.X + width;
        var panelBottom = pos.Y + height;
        var addonRight = addonPos.X + addonSize.X;
        var addonBottom = addonPos.Y + addonSize.Y;

        return pos.X < addonRight - margin
               && panelRight > addonPos.X + margin
               && pos.Y < addonBottom - margin
               && panelBottom > addonPos.Y + margin;
    }

    private void DrawContent()
    {
        var config = this.plugin.Configuration;

        ImGui.TextUnformatted("选择哪些航线参与随机：");
        ImGui.Separator();

        // 勾选框按菜单里实际出现的航线生成（用航线名作键：索引会随可选航线变化）
        foreach (var opt in this.options)
        {
            var selected = config.IsOceanRouteSelected(opt.Name);
            if (ImGui.Checkbox($"{opt.Name}##or{opt.ValueIndex}", ref selected))
            {
                config.SetOceanRouteSelected(opt.Name, selected);
                this.plugin.SaveConfig();
            }
        }

        ImGui.Separator();

        var pool = this.options.Where(o => config.IsOceanRouteSelected(o.Name)).ToList();
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

                // 提交必须在主线程。用列表序号（不是 AtkValue 位置）
                var submitted = Plugin.Framework.RunOnTick(() => this.menu.Select(picked.ListIndex)).GetAwaiter().GetResult();

                if (submitted)
                {
                    // 游戏随后会弹"要乘坐 XX 航线吗？"确认框，交给 TryAutoConfirm 处理
                    this.routeSubmittedAt = DateTime.UtcNow;

                    this.lastResult = $"已申请 {picked.Name}（{sourceTag}）";
                    this.lastWasError = false;
                    Plugin.ChatGui.Print(
                        $"[随机职业] 从{pool.Count}条航线（{poolNames}）中抽取到了{picked.Name}，已提交申请 —— {sourceTag}");
                    if (this.plugin.Configuration.AutoConfirmOceanRoute)
                        Plugin.ChatGui.Print("[随机职业] 等待确认窗口，将自动点「是」…");
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
