using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace RandomClassPicker;

/// <summary>每日随机任务（roulette）的一个条目快照。</summary>
public readonly record struct RouletteEntry(
    byte Id,
    string Name,
    bool IsCompleted,
    uint IconId);

/// <summary>
/// 读取"每日随机任务"列表与完成状态。
///
/// 完成状态来自客户端 <c>InstanceContent.IsRouletteIncomplete</c>（与游戏界面同源），
/// 名称取自 <c>ContentRoulette</c> 数据表（客户端本地化译名）。
///
/// 两条关键规则（都踩过坑）：
/// 1. **列表必须直接枚举表**，不能按 RowId 升序取行——表的内部顺序才是游戏内的显示顺序。
///    按 RowId 排会把"每日挑战"之类插到错误位置。
/// 2. **用 <c>ContentType.RowId == 1</c> 过滤"作战随机任务"**。
///    用名字前缀过滤只是权宜之计（会漏掉分类变化），游戏自己用的是这个字段。
/// </summary>
public sealed class DutyRouletteCatalog
{
    /// <summary>ContentRoulette.ContentType 中"随机任务"的取值。</summary>
    private const uint RouletteContentType = 1;

    /// <summary>
    /// 不参与随机的随机任务 ID：
    /// 4 = 行会令（内容性质与副本不同，按用户要求排除）。
    /// </summary>
    private static readonly HashSet<uint> ExcludedRouletteIds = [4];

    /// <summary>
    /// 游戏内任务搜索器的实际显示顺序（按用户截图核对）：
    /// 顶级迷宫 → 满级迷宫 → 拾级迷宫 → 练级迷宫 → 讨伐歼灭战 → 主线任务 →
    /// 行会令 → 团队任务 → 大型任务 → 指导者任务。
    /// 这个顺序**既不等于 RowId 升序，也不等于 ContentRoulette 表的内部顺序**，
    /// 所以只能显式指定。未列出的 ID 排在后面（按原顺序）。
    /// </summary>
    private static readonly uint[] DisplayOrder =
    [
        5,   // 顶级迷宫
        8,   // 满级迷宫
        2,   // 拾级迷宫
        1,   // 练级迷宫
        6,   // 讨伐歼灭战
        3,   // 主线任务
        4,   // 行会令
        15,  // 团队任务
        17,  // 大型任务
        9,   // 指导者任务
    ];

    /// <summary>
    /// 兜底白名单：即使 ContentType 判定为随机任务，名字不在这个前缀里的也排除。
    /// 用来防住"表里混入奇怪条目"的情况。
    /// </summary>
    private static readonly string[] AllowedPrefixes =
    [
        "随机任务",
        "每日挑战",
        "纷争前线",
        "水晶冲突",
    ];

    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly List<RouletteEntry> entries = [];
    private readonly List<RouletteEntry> allScanned = [];

    /// <summary>上次扫描时间，用于节流。</summary>
    private DateTime lastScan = DateTime.MinValue;

    /// <summary>
    /// 扫描节流间隔。
    /// 面板打开时每帧都会取数，而扫描要遍历整张表（含字符串分配）并对每个条目调用一次原生函数，
    /// 因此这里做时间节流；完成状态本身变化很慢，几秒的延迟无感。
    /// </summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);

    public DutyRouletteCatalog(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>上一次扫描到的随机任务（含完成状态），已按游戏内显示顺序排列，且已排除不参与随机的条目。</summary>
    public IReadOnlyList<RouletteEntry> Entries => this.entries;

    /// <summary>本次扫描到的全部条目（含被排除的），仅用于诊断。</summary>
    public IReadOnlyList<RouletteEntry> AllScanned => this.allScanned;

    /// <summary>某个随机任务是否不参与随机（例如行会令）。</summary>
    public static bool IsExcluded(uint id) => ExcludedRouletteIds.Contains(id);

    /// <summary>
    /// 重新扫描随机任务列表与完成状态。必须在主线程调用。
    /// </summary>
    /// <param name="force">
    /// true 时忽略节流立即扫描（用于用户主动刷新、抽签前取最新状态）。
    /// 面板每帧取数时传 false，由内部节流控制实际扫描频率。
    /// </param>
    public void Refresh(bool force = false)
    {
        if (!force && DateTime.UtcNow - this.lastScan < ScanInterval)
            return;

        this.lastScan = DateTime.UtcNow;
        this.entries.Clear();
        this.allScanned.Clear();

        try
        {
            var sheet = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentRoulette>();
            if (sheet is null)
                return;

            unsafe
            {
                var instanceContent = InstanceContent.Instance();
                if (instanceContent == null)
                    return;

                foreach (var row in sheet)
                {
                    var name = row.Name.ExtractText();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    // 游戏自己的分类：ContentType == 1 才是"作战随机任务"
                    if (row.ContentType.RowId != RouletteContentType)
                        continue;

                    // 兜底白名单
                    if (!IsAllowedName(name))
                    {
                        this.log.Debug($"[RandomClassPicker] 排除异常条目 id={row.RowId} 「{name}」");
                        continue;
                    }

                    var id = (byte)row.RowId;

                    // 0 表示"已完成"，这里转成更直观的 IsCompleted
                    var entry = new RouletteEntry(
                        Id: id,
                        Name: name,
                        IsCompleted: !instanceContent->IsRouletteIncomplete(id),
                        IconId: 0);

                    this.allScanned.Add(entry);

                    // 行会令等按需求不参与随机，直接不显示
                    if (IsExcluded(id))
                        continue;

                    this.entries.Add(entry);
                }
            }

            // 按游戏内实际顺序重排（见 DisplayOrder 注释）
            var ordered = this.entries.OrderBy(e => OrderKey(e.Id)).ToList();
            this.entries.Clear();
            this.entries.AddRange(ordered);

            this.log.Information(
                $"[RandomClassPicker] 随机任务扫描: 显示 {this.entries.Count} 个 / 共扫到 {this.allScanned.Count} 个: " +
                string.Join(" | ", this.entries.Select(e => $"{e.Id}:{e.Name}{(e.IsCompleted ? "(已完成)" : string.Empty)}")));
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[RandomClassPicker] 读取每日随机任务失败");
        }
    }

    /// <summary>排序键：DisplayOrder 中列出的按其在表中的位置，未列出的排在最后（保持相对顺序）。</summary>
    private static int OrderKey(uint id)
    {
        var idx = Array.IndexOf(DisplayOrder, id);
        return idx >= 0 ? idx : DisplayOrder.Length + (int)id;
    }

    private static bool IsAllowedName(string name)
        => AllowedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    public RouletteEntry? Get(byte id)
    {
        foreach (var e in this.entries)
        {
            if (e.Id == id)
                return e;
        }

        return null;
    }
}
