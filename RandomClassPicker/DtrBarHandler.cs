using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;

namespace RandomClassPicker;

/// <summary>
/// 服务器信息栏（DTR）上的快捷图标：不用打开插件窗口、也不用输指令，
/// 点一下就完成一次"随机抽选 + 切换"。
///
/// 注意：DTR 条目本身不能"浮在屏幕上"——它属于界面顶端的服务器信息栏。
/// 如果看不到，需要在游戏里解锁 HUD 并把该条目从隐藏列表里调出来（见 README）。
/// </summary>
public sealed class DtrBarHandler : IDisposable
{
    private const string EntryTitle = "Random Class Picker";

    /// <summary>FontAwesome 的 shuffle 图标（Dalamud 默认字体自带）。</summary>
    private const string Icon = "\uf074";

    private readonly Plugin plugin;
    private readonly IPluginLog log;
    private readonly IDtrBar dtrBar;
    private readonly IDtrBarEntry? entry;

    public DtrBarHandler(Plugin plugin, IPluginLog log, IDtrBar dtrBar)
    {
        this.plugin = plugin;
        this.log = log;
        this.dtrBar = dtrBar;

        try
        {
            this.entry = dtrBar.Get(EntryTitle);
            this.entry.OnClick += this.OnClicked;
            this.entry.Tooltip = this.BuildTooltip();
            this.Refresh();
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[RandomClassPicker] 创建 DTR 快捷图标失败");
        }
    }

    /// <summary>抽签进行中时在图标上显示省略号。</summary>
    public void Refresh()
    {
        if (this.entry is null)
            return;

        var showEntry = this.plugin.Configuration.ShowDtrEntry;
        this.entry.Shown = showEntry;
        if (!showEntry)
            return;

        var showIcon = this.plugin.Configuration.ShowDtrIcon;
        var iconPart = showIcon ? Icon + " " : string.Empty;

        var label = this.plugin.Window.IsBusy
            ? "…"
            : this.plugin.Window.LastResult?.JobName ?? this.plugin.LastDrawnJobName ?? string.Empty;

        this.entry.Text = iconPart + label;
        this.entry.Tooltip = this.BuildTooltip();
    }

    private string BuildTooltip()
    {
        if (!this.plugin.Configuration.ShowDtrEntry)
            return "快捷图标已关闭";

        var last = this.plugin.Window.LastResult;
        var lastLine = last is { } r
            ? $"\n上次: {r.JobName}（{r.RoleName}），该职业累计 {r.JobDrawCount} 次"
            : string.Empty;

        return $"左键：随机抽一个职业并切换{lastLine}\n按住 Ctrl 左键：打开插件窗口\n（可在设置里关闭此图标）";
    }

    /// <summary>
    /// 是否按住了修饰键。
    /// 用数值判断而不是 HasFlag(None)：后者对任何值都返回 true（零值按位与恒为 0）。
    /// 顺带完全避开"枚举类型不匹配"的坑。
    /// </summary>
    private static bool HasModifier(ClickModifierKeys keys) => (int)keys != 0;

    private void OnClicked(DtrInteractionEvent ev)
    {
        try
        {
            // 注意：DtrInteractionEvent 只提供 ModifierKeys，拿不到鼠标按键，
            // 所以用"按住 Ctrl 点击"来表示"打开窗口"，普通点击直接抽签。
            //
            // 两个坑：
            // 1) ModifierKeys 的类型是 Dalamud.Game.Gui.Dtr.ClickModifierKeys，不是 ImGuiKey。
            //    Enum.HasFlag 要求两边是同一个枚举类型，传错类型会在**运行时**抛
            //    ArgumentException（编译期完全不报错）。
            // 2) 不能用 HasFlag(None)——零值判定恒为真，会把普通点击也当成"按住修饰键"。
            if (HasModifier(ev.ModifierKeys))
            {
                this.plugin.Window.IsOpen = true;
                return;
            }

            if (this.plugin.Window.IsBusy)
            {
                Plugin.ChatGui.Print("[随机职业] 上一次抽签还在进行中…");
                return;
            }

            this.plugin.RollAndSwitch();
        }
        catch (Exception ex)
        {
            // 之前这个异常只写进日志（而且没有弹提示），导致"点了没反应"很难定位。
            // 现在同时给聊天栏反馈，避免静默失败。
            this.log.Error(ex, "[RandomClassPicker] DTR 快捷抽签失败");
            Plugin.ChatGui.PrintError($"[随机职业] 快捷图标操作失败: {ex.Message}");
        }
        finally
        {
            this.Refresh();
        }
    }

    public void Dispose()
    {
        try
        {
            if (this.entry is not null)
                this.entry.OnClick -= this.OnClicked;

            this.dtrBar.Remove(EntryTitle);
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[RandomClassPicker] 移除 DTR 快捷图标失败");
        }
    }
}
