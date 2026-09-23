using Dalamud.Game.Gui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace RandomClassPicker;

/// <summary>申请航线菜单里的一条航线。</summary>
/// <param name="ValueIndex">该航线文本在 AtkValue 数组里的位置（仅用于诊断）。</param>
/// <param name="ListIndex">在菜单列表里的序号（从 0 开始，表头与"取消"不计入）——选中时用这个。</param>
/// <param name="Name">航线名。</param>
/// <param name="IsNearSea">是近海（名字里不带"远"）。</param>
public readonly record struct RouteOption(int ValueIndex, int ListIndex, string Name, bool IsNearSea);

/// <summary>
/// 出海垂钓的"申请航线"菜单访问器。
///
/// 这个菜单用的是通用的 <c>SelectString</c> 窗口（限定职业任务等也用同一个窗口），
/// 所以**必须按内容判定**：菜单首行文本是「要乘坐哪条航线？」时才是申请航线。
/// 选项文本从 <c>AtkValue</c> 的字符串值里取（type=8 = String）。
/// </summary>
public sealed class OceanFishingMenu
{
    /// <summary>申请航线菜单的识别标记（首行提示文本）。</summary>
    private const string RouteMenuMarker = "要乘坐哪条航线";

    private const string AddonName = "SelectString";

    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    public OceanFishingMenu(IGameGui gameGui, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.log = log;
    }

    public unsafe bool IsOpen
    {
        get
        {
            var addon = this.gameGui.GetAddonByName(AddonName, 1);
            if (addon.IsNull)
                return false;

            var unit = (AtkUnitBase*)addon.Address;
            if (unit == null || !unit->IsVisible)
                return false;

            // 首行提示文本是识别标记
            var header = unit->GetTextNodeById(2);
            if (header == null)
                return false;

            return header->NodeText.ToString().Contains(RouteMenuMarker, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 读当前菜单里的航线选项。菜单没开或不是申请航线时返回空。
    ///
    /// 重要：`AtkValue` 里的字符串项**不等于**选项列表。实测申请航线菜单里有 4 项：
    ///   [1] "要乘坐哪条航线?"   ← 表头，不是可选项
    ///   [2] "乘坐罗塔诺海航线。" ← 真选项
    ///   [3] "乘坐红玉海航线。"   ← 真选项
    ///   [4] "取消"             ← 取消项，不是航线
    /// 所以必须过滤出真正的航线（名字里含「航线」），
    /// 并且**保留它们在 AtkValue 里的原始位置**——菜单索引就是那个位置，
    /// 过滤后重新编号会让 FireCallback 选错项。
    /// </summary>
    public unsafe List<RouteOption> ReadOptions()
    {
        var result = new List<RouteOption>();

        try
        {
            var addon = this.gameGui.GetAddonByName(AddonName, 1);
            if (addon.IsNull)
                return result;

            var unit = (AtkUnitBase*)addon.Address;
            if (unit == null || !unit->IsVisible)
                return result;

            var header = unit->GetTextNodeById(2);
            if (header == null || !header->NodeText.ToString().Contains(RouteMenuMarker, StringComparison.Ordinal))
                return result;

            for (var i = 0; i < unit->AtkValuesCount; i++)
            {
                var v = unit->AtkValues[i];
                if (v.Type != AtkValueType.String || v.String.Value == null)
                    continue;

                var text = v.String.ToString();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                // 只保留真正的航线；表头与"取消"都排除
                if (!IsRouteName(text))
                    continue;

                result.Add(new RouteOption(
                    ValueIndex: i,          // AtkValue 位置（诊断用）
                    ListIndex: result.Count, // 列表序号：按有效航线顺序递增，选中时用它
                    Name: text,
                    IsNearSea: IsNearSeaName(text)));
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[RandomClassPicker] 读取申请航线菜单失败");
        }

        return result;
    }

    /// <summary>
    /// 用 PopupMenu.EntryNames 读取菜单项。
    ///
    /// 这是**游戏自己维护的菜单项数组**，索引就是游戏内部的菜单索引，
    /// 比从 AtkValues 反推可靠得多（AtkValues 里混着表头等非选项）。
    /// 实测一个三项链路菜单：EntryCount=3、EntryNames=[航线1, 航线2, "取消"]。
    /// </summary>
    public unsafe List<RouteOption> ReadOptionsFromEntries()
    {
        var result = new List<RouteOption>();

        try
        {
            var addon = this.gameGui.GetAddonByName(AddonName, 1);
            if (addon.IsNull)
                return result;

            var selectString = (AddonSelectString*)addon.Address;
            if (selectString == null || !selectString->AtkUnitBase.IsVisible)
                return result;

            var popup = &selectString->PopupMenu;
            if (popup->EntryNames == null || popup->EntryCount <= 0)
                return result;

            for (var i = 0; i < popup->EntryCount; i++)
            {
                var name = popup->EntryNames[i].ToString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                // 只保留真正的航线；"取消"排除
                if (!IsRouteName(name))
                    continue;

                // 这里 EntryNames 的索引 i 就是游戏菜单索引，直接用
                result.Add(new RouteOption(
                    ValueIndex: i,
                    ListIndex: i,
                    Name: name,
                    IsNearSea: IsNearSeaName(name)));
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[RandomClassPicker] 读取 EntryNames 失败");
        }

        return result;
    }

    /// <summary>
    /// 是不是一条可选航线。
    ///
    /// 注意**不能**只用「包含航线」判断：菜单表头是「要乘坐哪条航线?」，
    /// 它同样含「航线」，却不是一个可选项（上一版就是这么漏进去的）。
    /// 真正的选项形如「乘坐罗塔诺海航线。」，都以「乘坐」开头；
    /// 「取消」则两者都不满足。
    /// </summary>
    public static bool IsRouteName(string text)
        => text.StartsWith("乘坐", StringComparison.Ordinal)
           && text.Contains("航线", StringComparison.Ordinal);

    /// <summary>
    /// 判定某个菜单项是不是"取消"。
    /// 抽签时只在航线里抽，所以正常不会碰到；保留这个判断是为了防御性检查。
    /// </summary>
    public static bool IsCancelEntry(string text)
        => !IsRouteName(text);

    /// <summary>近海还是远海。名字里带"远"的算远海，否则算近海。</summary>
    public static bool IsNearSeaName(string name)
        => !name.Contains('远');

    /// <summary>
    /// 自动确认"是否乘坐该航线"的确认窗口。
    ///
    /// 流程实测为两段：先在 SelectString 里选航线，游戏再弹一个 SelectYesno 询问
    /// 「要乘坐红玉海航线吗？」。这个窗口也要点「是」才算真正提交。
    ///
    /// 只在**我们刚提交过航线申请**时才会调用（由调用方用短时效标记控制），
    /// 不会误点其它地方的确认框。
    /// </summary>
    public unsafe bool ConfirmYesNo()
    {
        try
        {
            var addon = this.gameGui.GetAddonByName("SelectYesno", 1);
            if (addon.IsNull)
                return false;

            var unit = (AtkUnitBase*)addon.Address;
            if (unit == null || !unit->IsVisible)
                return false;

            // SelectYesno 的惯例：回调参数 (0, 1) = 点「是」
            var values = stackalloc AtkValue[2];
            values[0].Type = AtkValueType.Int;
            values[0].Int = 0;
            values[1].Type = AtkValueType.Int;
            values[1].Int = 1;

            unit->FireCallback(2u, values, true);
            this.log.Information("[RandomClassPicker] 已自动确认航线申请（点「是」）");
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[RandomClassPicker] 自动确认航线申请失败");
            return false;
        }
    }

    /// <summary>确认窗口当前是否可见。</summary>
    public unsafe bool IsConfirmOpen()
    {
        try
        {
            var addon = this.gameGui.GetAddonByName("SelectYesno", 1);
            if (addon.IsNull)
                return false;

            var unit = (AtkUnitBase*)addon.Address;
            return unit != null && unit->IsVisible;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>候选触发方式，用于诊断到底哪种能真正选中。</summary>
    public enum TriggerMethod
    {
        /// <summary>列表组件 SelectItem(index, true)：选中并派发事件。</summary>
        ListSelectDispatch,

        /// <summary>列表组件 SelectItem(index, false)：只选中不派发。</summary>
        ListSelectOnly,

        /// <summary>FireCallback(2u, [{-1}, {index}], true)（早期猜法）。</summary>
        FireCallbackMinusOne,

        /// <summary>FireCallback(2u, [{index}, {1}], true)。</summary>
        FireCallbackIndexOne,

        /// <summary>FireCallback(1u, [{index}], true)：单参数最常见形式。</summary>
        FireCallbackSingle,
    }

    /// <summary>按指定方式触发选中，并把结果记进诊断时间线。</summary>
    public unsafe bool Trigger(int listIndex, TriggerMethod method, OceanFishingProbe probe)
    {
        try
        {
            var addon = this.gameGui.GetAddonByName(AddonName, 1);
            if (addon.IsNull)
            {
                probe.Trace($"Trigger[{method}]: SelectString 未打开，跳过");
                return false;
            }

            var selectString = (AddonSelectString*)addon.Address;
            if (selectString == null)
            {
                probe.Trace($"Trigger[{method}]: 地址为空");
                return false;
            }

            var unit = &selectString->AtkUnitBase;

            switch (method)
            {
                case TriggerMethod.ListSelectDispatch:
                case TriggerMethod.ListSelectOnly:
                {
                    var list = selectString->PopupMenu.List;
                    if (list == null)
                    {
                        probe.Trace($"Trigger[{method}]: List 为 null");
                        return false;
                    }

                    list->SelectItem(listIndex, method == TriggerMethod.ListSelectDispatch);
                    probe.Trace($"Trigger[{method}]: 已调用 SelectItem({listIndex}, " +
                        $"{(method == TriggerMethod.ListSelectDispatch ? "true" : "false")})");
                    return true;
                }

                case TriggerMethod.FireCallbackMinusOne:
                {
                    var values = stackalloc AtkValue[2];
                    values[0].Type = AtkValueType.Int;
                    values[0].Int = -1;
                    values[1].Type = AtkValueType.Int;
                    values[1].Int = listIndex;
                    unit->FireCallback(2u, values, true);
                    probe.Trace($"Trigger[{method}]: FireCallback(2u, [-1, {listIndex}], true)");
                    return true;
                }

                case TriggerMethod.FireCallbackIndexOne:
                {
                    var values = stackalloc AtkValue[2];
                    values[0].Type = AtkValueType.Int;
                    values[0].Int = listIndex;
                    values[1].Type = AtkValueType.Int;
                    values[1].Int = 1;
                    unit->FireCallback(2u, values, true);
                    probe.Trace($"Trigger[{method}]: FireCallback(2u, [{listIndex}, 1], true)");
                    return true;
                }

                case TriggerMethod.FireCallbackSingle:
                {
                    var values = stackalloc AtkValue[1];
                    values[0].Type = AtkValueType.Int;
                    values[0].Int = listIndex;
                    unit->FireCallback(1u, values, true);
                    probe.Trace($"Trigger[{method}]: FireCallback(1u, [{listIndex}], true)");
                    return true;
                }

                default:
                    probe.Trace($"Trigger[{method}]: 未实现");
                    return false;
            }
        }
        catch (Exception ex)
        {
            probe.Trace($"Trigger[{method}]: 异常 {ex.GetType().Name}: {ex.Message}");
            this.log.Error(ex, $"[RandomClassPicker] Trigger[{method}] 失败");
            return false;
        }
    }

    /// <summary>
    /// 提交选择（正式路径）。
    ///
    /// 实测结论（/rcp ikdtest 的诊断数据）：
    ///   - <c>List->SelectItem(index, true/false)</c>：菜单无任何变化，无效
    ///   - <c>FireCallback(2u, [-1, index], true)</c>（两个参数）：无效
    ///   - <c>FireCallback(1u, [index], true)</c>（**单个 Int 参数**）：菜单被关闭，选择被受理 ★
    ///
    /// 这与确认框 dump 里 `AtkValues[4] Int=1` 的形式一致——游戏这套回调习惯传单个 Int。
    /// 随后游戏会弹「要乘坐 XX 航线吗？」，由叠加层的自动确认处理。
    /// </summary>
    /// <param name="menuIndex">
    /// 该航线在菜单里的索引。用 <c>PopupMenu.EntryNames</c> 的数组位置（即 <c>ListIndex</c>），
    /// 注意不是 AtkValues 数组位置。
    /// </param>
    public unsafe bool Select(int menuIndex)
    {
        try
        {
            var addon = this.gameGui.GetAddonByName(AddonName, 1);
            if (addon.IsNull)
                return false;

            var unit = (AtkUnitBase*)addon.Address;
            if (unit == null || !unit->IsVisible)
                return false;

            var values = stackalloc AtkValue[1];
            values[0].Type = AtkValueType.Int;
            values[0].Int = menuIndex;

            unit->FireCallback(1u, values, true);
            this.log.Information($"[RandomClassPicker] 已提交航线选择，菜单索引 {menuIndex}");
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[RandomClassPicker] 提交航线选择失败");
            return false;
        }
    }
}
