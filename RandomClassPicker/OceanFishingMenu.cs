using Dalamud.Game.Gui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace RandomClassPicker;

/// <summary>申请航线菜单里的一条航线。</summary>
public readonly record struct RouteOption(int Index, string Name, bool IsNearSea);

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

    /// <summary>读当前菜单里的航线选项。菜单没开或不是申请航线时返回空。</summary>
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

            // 选项是 AtkValue 里的字符串项；按出现顺序编号即为菜单索引
            var menuIndex = 0;
            for (var i = 0; i < unit->AtkValuesCount; i++)
            {
                var v = unit->AtkValues[i];
                if (v.Type != AtkValueType.String || v.String.Value == null)
                    continue;

                // CStringPointer 自带 UTF-8 解码的 ToString
                var text = v.String.ToString();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                result.Add(new RouteOption(menuIndex, text, IsNearSeaName(text)));
                menuIndex++;
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[RandomClassPicker] 读取申请航线菜单失败");
        }

        return result;
    }

    /// <summary>近海还是远海。名字里带"远"的算远海，否则算近海。</summary>
    public static bool IsNearSeaName(string name)
        => !name.Contains('远');

    /// <summary>
    /// 提交选择：FireCallback 第 2 个参数为 -1、第 3 个参数为选项索引。
    /// 返回是否成功触发（仅代表调用发出，不代表游戏已受理）。
    /// </summary>
    public unsafe bool Select(int menuIndex)
    {
        try
        {
            var addon = this.gameGui.GetAddonByName(AddonName, 1);
            if (addon.IsNull)
                return false;

            var unit = (AtkUnitBase*)addon.Address;
            if (unit == null)
                return false;

            var values = stackalloc AtkValue[2];
            values[0].Type = AtkValueType.Int;
            values[0].Int = -1;
            values[1].Type = AtkValueType.Int;
            values[1].Int = menuIndex;

            unit->FireCallback(2u, values, true);
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
