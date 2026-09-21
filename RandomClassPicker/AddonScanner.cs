using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace RandomClassPicker;

/// <summary>
/// 窗口发现工具：探测一批候选窗口名，记录哪些当前可见，并写到本地文件。
///
/// 用途：出海垂钓的"申请航线"窗口在客户端结构里没有专门的 addon 类型
/// （DistantSeas / AutoHook 都不处理它），无法从代码侧确定它叫什么。
/// 打开那个窗口时跑一次 `/rcp scan`，就能得到确切的名字与位置。
///
/// 注意：Dalamud 的 IGameGui 没有"枚举全部窗口"的 API（AtkStage.UnitManager 也不可用），
/// 所以只能按候选名单探测。
/// </summary>
public sealed class AddonScanner
{
    /// <summary>候选窗口名：涵盖 NPC 对话、各种选择菜单、以及可能的相关窗口。</summary>
    private static readonly string[] Candidates =
    [
        "SelectString", "SelectIconString", "SelectOk", "SelectYesno", "Selectable",
        "Talk", "TalkSubtitle", "CutSceneSelectString", "CutSceneSelectString2",
        "ContentsFinder", "ContentsFinderConfirm", "ContentsFinderMenu",
        "IKDFishingLog", "IKDMission", "IKDResult", "IKDRoute", "IKDRouteSelect",
        "FishingNote", "FishGuide2", "SpearFishing", "FishingLog",
        "QuestJournal", "ScenarioTree", "SelectStringMenu",
        "OceanFishing", "DeepSeaFishing", "FishingBoat",
        "Journal", "ContentsTutorial", "Ornament", "Character", "Inventory",
    ];

    private const int ScanDurationMs = 3000;

    private readonly IPluginLog log;
    private readonly IGameGui gameGui;
    private readonly List<(string Name, float X, float Y, float W, float H)> hits = [];

    private bool scanning;
    private DateTime startedAt;

    public AddonScanner(IPluginLog log, IGameGui gameGui)
    {
        this.log = log;
        this.gameGui = gameGui;
    }

    public bool IsScanning => this.scanning;

    /// <summary>开始扫描（由 /rcp scan 触发）。</summary>
    public void Start()
    {
        this.hits.Clear();
        this.scanning = true;
        this.startedAt = DateTime.UtcNow;
    }

    /// <summary>每帧调用：在扫描期间记录所有命中的候选窗口。</summary>
    public unsafe void Tick()
    {
        if (!this.scanning)
            return;

        if ((DateTime.UtcNow - this.startedAt).TotalMilliseconds > ScanDurationMs)
        {
            this.scanning = false;
            return;
        }

        try
        {
            foreach (var name in Candidates)
            {
                if (this.hits.Any(h => h.Name == name))
                    continue;

                var addon = this.gameGui.GetAddonByName(name, 1);
                if (addon.IsNull)
                    continue;

                var unit = (AtkUnitBase*)addon.Address;
                if (unit == null || !unit->IsVisible)
                    continue;

                var scale = unit->Scale <= 0f ? 1f : unit->Scale;
                this.hits.Add((
                    name,
                    unit->X,
                    unit->Y,
                    (unit->RootNode == null ? 0f : unit->RootNode->Width) * scale,
                    (unit->RootNode == null ? 0f : unit->RootNode->Height) * scale));
            }
        }
        catch (Exception ex)
        {
            this.scanning = false;
            this.log.Debug(ex, "[RandomClassPicker] 扫描窗口失败");
        }
    }

    /// <summary>把扫描结果写成文本。</summary>
    public string WriteResult()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"===== 窗口扫描 {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} =====");
        sb.AppendLine($"候选名单 {Candidates.Length} 个，其中当前可见 {this.hits.Count} 个：");
        sb.AppendLine();

        if (this.hits.Count == 0)
        {
            sb.AppendLine("（没有命中任何候选窗口。说明该窗口不在候选名单里，需要扩充名单。）");
        }
        else
        {
            foreach (var h in this.hits.OrderByDescending(x => x.W * x.H))
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0,-26} pos=({1,6:F0},{2,6:F0})  size=({3,6:F0}x{4,6:F0})",
                    h.Name, h.X, h.Y, h.W, h.H));
            }
        }

        // SelectString 的详细内容：判断它是不是"申请航线"菜单，以及选项文本
        sb.AppendLine();
        sb.Append(this.DumpSelectString());

        return sb.ToString();
    }

    /// <summary>
    /// 把 SelectString 窗口的菜单项文本读出来。
    /// 目的：确认这个通用文本选择菜单里装的是不是航线选项，以及怎么读取它们。
    /// 文本从各文本节点的 NodeText 读取（比解析 AtkValue 联合体更贴近界面实际显示）。
    /// </summary>
    private unsafe string DumpSelectString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("--- SelectString 详细内容 ---");

        try
        {
            var addon = this.gameGui.GetAddonByName("SelectString", 1);
            if (addon.IsNull)
            {
                sb.AppendLine("SelectString 当前未打开。");
                return sb.ToString();
            }

            var unit = (AtkUnitBase*)addon.Address;
            if (unit == null)
            {
                sb.AppendLine("SelectString 地址为空。");
                return sb.ToString();
            }

            sb.AppendLine($"IsVisible={unit->IsVisible} AtkValuesCount={unit->AtkValuesCount}");
            sb.AppendLine("文本节点（NodeId 2..16）:");

            var found = 0;
            for (uint nodeId = 2; nodeId <= 16; nodeId++)
            {
                var textNode = unit->GetTextNodeById(nodeId);
                if (textNode == null)
                    continue;

                var text = textNode->NodeText.ToString();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                sb.AppendLine($"  node {nodeId,2}: \"{text}\"");
                found++;
            }

            if (found == 0)
                sb.AppendLine("  （没有读到文本，说明菜单项不在 NodeId 2..16 范围内）");

            // 顺便把 AtkValues 的类型列出来（判断选项是不是塞在 AtkValue 里）
            sb.AppendLine("AtkValues 类型一览:");
            for (var i = 0; i < unit->AtkValuesCount && i < 24; i++)
            {
                var v = unit->AtkValues[i];
                var n = v.Int;
                sb.AppendLine($"  [{i,2}] type={(int)v.Type} int={n}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"dump 失败: {ex.GetType().Name}: {ex.Message}");
        }

        return sb.ToString();
    }

    /// <summary>扫描期间的可见提示。</summary>
    public void DrawScanIndicator()
    {
        if (!this.scanning)
            return;

        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(vp.Size.X / 2f - 170f, 60f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(340f, 0f), ImGuiCond.Always);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoNav;

        if (ImGui.Begin("##rcpScanIndicator", flags))
        {
            ImGui.TextColored(
                new Vector4(1f, 0.9f, 0.3f, 1f),
                $"正在记录当前打开的窗口… 已命中 {this.hits.Count} 个");
            ImGui.TextDisabled("3 秒后自动结束");
        }

        ImGui.End();
    }
}
