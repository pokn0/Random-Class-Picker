using System.Globalization;
using Dalamud.Game.Gui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace RandomClassPicker;

/// <summary>
/// 出海垂钓菜单的深度诊断：把 PopupMenu 的真实结构（EntryCount / EntryNames）、
/// 列表组件状态、以及"选中后确认框有没有出现"的时间线全部记到文件。
///
/// 之所以需要它：选中航线这一步试过 FireCallback 猜参数、AtkComponentList.SelectItem
/// 两种方式都没成功，必须拿到真实结构才能定位，而不是继续试。
/// </summary>
public sealed class OceanFishingProbe
{
    private const string AddonName = "SelectString";
    private const string YesNoName = "SelectYesno";

    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly List<string> trace = [];

    public OceanFishingProbe(IGameGui gameGui, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.log = log;
    }

    /// <summary>已尝试的触发方式数量（供时间线判断"上一轮之后"）。</summary>
    public int SeenMethods { get; set; }

    /// <summary>第一个让确认框出现的触发方式；null 表示都没成功。</summary>
    public string? FoundWorkingMethod { get; set; }

    /// <summary>记录一条时间线事件。</summary>
    public void Trace(string message)
        => this.trace.Add($"+{(DateTime.UtcNow - this.startedAt).TotalMilliseconds,7:F0}ms  {message}");

    private DateTime startedAt = DateTime.UtcNow;

    /// <summary>开始一轮诊断。</summary>
    public void Start()
    {
        this.trace.Clear();
        this.startedAt = DateTime.UtcNow;
    }

    /// <summary>生成完整 dump 文本。</summary>
    public unsafe string BuildDump(string scenario)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"===== 出海垂钓诊断 {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} =====");
        sb.AppendLine($"场景: {scenario}");
        sb.AppendLine();

        // 1) SelectString / PopupMenu 的真实结构
        sb.AppendLine("--- SelectString / PopupMenu 结构 ---");
        try
        {
            var addon = this.gameGui.GetAddonByName(AddonName, 1);
            if (addon.IsNull)
            {
                sb.AppendLine("SelectString 当前未打开。");
            }
            else
            {
                var selectString = (AddonSelectString*)addon.Address;
                if (selectString == null)
                {
                    sb.AppendLine("地址为空。");
                }
                else
                {
                    var popup = &selectString->PopupMenu;
                    sb.AppendLine($"IsVisible={selectString->AtkUnitBase.IsVisible}");
                    sb.AppendLine($"PopupMenu.EntryCount = {popup->EntryCount}");
                    sb.AppendLine($"PopupMenu.List       = {(popup->List == null ? "null" : "有效")}");
                    sb.AppendLine($"PopupMenu.Owner      = {(popup->Owner == null ? "null" : "有效")}");

                    // EntryNames：游戏自己维护的菜单项名字数组，长度应与 EntryCount 一致
                    sb.AppendLine("PopupMenu.EntryNames:");
                    if (popup->EntryNames == null)
                    {
                        sb.AppendLine("  (null)");
                    }
                    else
                    {
                        var count = popup->EntryCount;
                        for (var i = 0; i < count && i < 32; i++)
                        {
                            var name = popup->EntryNames[i].ToString();
                            sb.AppendLine($"  [{i,2}] \"{name}\"");
                        }
                    }

                    // 对照 AtkValues 里的字符串项（我之前用这个当选项列表）
                    sb.AppendLine("AtkValues 字符串项（对照用）:");
                    var valueIdx = 0;
                    for (var i = 0; i < selectString->AtkUnitBase.AtkValuesCount; i++)
                    {
                        var v = selectString->AtkUnitBase.AtkValues[i];
                        if (v.Type != AtkValueType.String || v.String.Value == null)
                            continue;

                        sb.AppendLine($"  valueIdx[{valueIdx,2}] atkIndex{i,2}: \"{v.String.ToString()}\"");
                        valueIdx++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"dump 失败: {ex.GetType().Name}: {ex.Message}");
        }

        sb.AppendLine();

        // 2) 确认窗口状态
        sb.AppendLine("--- SelectYesno 状态 ---");
        try
        {
            var addon = this.gameGui.GetAddonByName(YesNoName, 1);
            if (addon.IsNull)
            {
                sb.AppendLine("SelectYesno 未打开。");
            }
            else
            {
                var unit = (AtkUnitBase*)addon.Address;
                sb.AppendLine($"IsVisible={unit->IsVisible}");
                var node = unit->GetTextNodeById(2);
                if (node != null)
                    sb.AppendLine($"node 2: \"{node->NodeText.ToString()}\"");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"dump 失败: {ex.GetType().Name}: {ex.Message}");
        }

        sb.AppendLine();

        // 3) 时间线：看操作后各窗口的真实变化
        sb.AppendLine("--- 时间线 ---");
        if (this.trace.Count == 0)
            sb.AppendLine("(无记录)");
        else
            foreach (var line in this.trace)
                sb.AppendLine(line);

        return sb.ToString();
    }
}
