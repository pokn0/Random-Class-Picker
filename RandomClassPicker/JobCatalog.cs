using Dalamud.Plugin.Services;

namespace RandomClassPicker;

/// <summary>职业职能分类。生产/采集被单独分开，本插件不会把它们放进随机池。</summary>
public enum JobRole
{
    /// <summary>防护职业（坦克）</summary>
    Tank,

    /// <summary>治疗职业</summary>
    Healer,

    /// <summary>近战职业</summary>
    MeleeDps,

    /// <summary>远程物理职业</summary>
    PhysicalRangedDps,

    /// <summary>远程魔法职业</summary>
    MagicalRangedDps,

    /// <summary>能工巧匠（生产，不参与随机）</summary>
    Crafter,

    /// <summary>大地使者（采集，不参与随机）</summary>
    Gatherer,

    /// <summary>其它/无法判定</summary>
    Unknown,
}

/// <summary>目录里的一个职业条目。</summary>
public readonly record struct JobInfo(
    uint Id,
    string Name,
    string Abbreviation,
    JobRole Role,
    bool IsLimitedJob)
{
    /// <summary>是不是战斗职业（防护/治疗/近战/远程物理/远程魔法）。</summary>
    public bool IsCombat => this.Role is JobRole.Tank or JobRole.Healer or JobRole.MeleeDps
        or JobRole.PhysicalRangedDps or JobRole.MagicalRangedDps;

    /// <summary>是不是特殊职业（限定职业：青魔法师、驯兽师）。</summary>
    public bool IsSpecial => this.IsLimitedJob;
}

/// <summary>
/// 职业目录：启动时从游戏数据表读取职业清单与本地化名称/缩写，
/// 职能则依据 <see cref="RoleTable"/> 的 ID 表判定。
///
/// 为什么职能用 ID 表而不是读游戏分类：
/// 游戏 ClassJobCategory 的引用解析在本机验证下来回出错（用 GetRow(RowId) 取到的不是
/// 该职业真正的职能行，导致防护职业混进 19 个职业），而沙箱里跑不了游戏无法继续比对。
/// 职能本身是极其稳定的游戏设定（新职业只会在资料片加入），用显式 ID 表反而更可靠、可审查。
/// 名称仍然从游戏数据读，保证国服译名正确。
/// </summary>
public sealed class JobCatalog
{
    /// <summary>
    /// 职业 ID -> 职能。ID 与国服客户端 ClassJob 表一致，已按实测输出核对。
    /// 新资料片加职业时在这里补一行即可。
    /// </summary>
    private static readonly Dictionary<uint, JobRole> RoleTable = new()
    {
        // ---- 防护职业（4）----
        [19] = JobRole.Tank,   // 骑士
        [21] = JobRole.Tank,   // 战士
        [32] = JobRole.Tank,   // 暗黑骑士
        [37] = JobRole.Tank,   // 绝枪战士

        // ---- 治疗职业（4）----
        [24] = JobRole.Healer, // 白魔法师
        [28] = JobRole.Healer, // 学者
        [33] = JobRole.Healer, // 占星术士
        [40] = JobRole.Healer, // 贤者

        // ---- 近战职业（6）----
        [20] = JobRole.MeleeDps, // 武僧
        [22] = JobRole.MeleeDps, // 龙骑士
        [30] = JobRole.MeleeDps, // 忍者
        [34] = JobRole.MeleeDps, // 武士
        [39] = JobRole.MeleeDps, // 钐镰客
        [41] = JobRole.MeleeDps, // 蝰蛇剑士

        // ---- 远程物理职业（3）----
        [23] = JobRole.PhysicalRangedDps, // 吟游诗人
        [31] = JobRole.PhysicalRangedDps, // 机工士
        [38] = JobRole.PhysicalRangedDps, // 舞者

        // ---- 远程魔法职业（3）----
        [25] = JobRole.MagicalRangedDps, // 黑魔法师
        [27] = JobRole.MagicalRangedDps, // 召唤师
        [35] = JobRole.MagicalRangedDps, // 赤魔法师
        [42] = JobRole.MagicalRangedDps, // 绘灵法师

        // ---- 特殊职业（限定职业）----
        [36] = JobRole.MagicalRangedDps, // 青魔法师
        [43] = JobRole.MeleeDps,         // 驯兽师

        // ---- 基础职业（会被特职取代，但仍是可切换的套装）----
        [1] = JobRole.Tank,                // 剑术师
        [3] = JobRole.Tank,                // 斧术师
        [6] = JobRole.Healer,              // 幻术师
        [2] = JobRole.MeleeDps,            // 格斗家
        [4] = JobRole.MeleeDps,            // 枪术师
        [29] = JobRole.MeleeDps,           // 双剑师
        [5] = JobRole.PhysicalRangedDps,   // 弓箭手
        [7] = JobRole.MagicalRangedDps,    // 咒术师
        [26] = JobRole.MagicalRangedDps,   // 秘术师

        // ---- 能工巧匠（8-15）----
        [8] = JobRole.Crafter,  // 木工师
        [9] = JobRole.Crafter,  // 锻铁师
        [10] = JobRole.Crafter, // 铸甲师
        [11] = JobRole.Crafter, // 雕金师
        [12] = JobRole.Crafter, // 制革师
        [13] = JobRole.Crafter, // 裁缝师
        [14] = JobRole.Crafter, // 炼金师
        [15] = JobRole.Crafter, // 烹调师

        // ---- 大地使者（16-18）----
        [16] = JobRole.Gatherer, // 采矿工
        [17] = JobRole.Gatherer, // 园艺工
        [18] = JobRole.Gatherer, // 捕鱼人
    };

    /// <summary>
    /// 名称兜底表：万一某个职业的 ID 不在 <see cref="RoleTable"/> 里（例如版本更新加了新职业），
    /// 就按中文名再判一次，避免它掉进 Unknown 而从池子里消失。
    /// </summary>
    private static readonly (string Keyword, JobRole Role)[] NameFallback =
    [
        ("骑士", JobRole.Tank), ("战士", JobRole.Tank), ("暗黑骑士", JobRole.Tank), ("绝枪", JobRole.Tank),
        ("剑术师", JobRole.Tank), ("斧术师", JobRole.Tank),
        ("白魔", JobRole.Healer), ("学者", JobRole.Healer), ("占星", JobRole.Healer), ("贤者", JobRole.Healer),
        ("幻术师", JobRole.Healer),
        ("武僧", JobRole.MeleeDps), ("龙骑士", JobRole.MeleeDps), ("忍者", JobRole.MeleeDps),
        ("武士", JobRole.MeleeDps), ("钐镰", JobRole.MeleeDps), ("蝰蛇", JobRole.MeleeDps), ("驯兽", JobRole.MeleeDps),
        ("格斗家", JobRole.MeleeDps), ("枪术师", JobRole.MeleeDps), ("双剑师", JobRole.MeleeDps),
        ("吟游诗人", JobRole.PhysicalRangedDps), ("机工士", JobRole.PhysicalRangedDps), ("舞者", JobRole.PhysicalRangedDps),
        ("弓箭手", JobRole.PhysicalRangedDps),
        ("黑魔法师", JobRole.MagicalRangedDps), ("召唤师", JobRole.MagicalRangedDps), ("赤魔法师", JobRole.MagicalRangedDps),
        ("绘灵法师", JobRole.MagicalRangedDps), ("青魔法师", JobRole.MagicalRangedDps),
        ("咒术师", JobRole.MagicalRangedDps), ("秘术师", JobRole.MagicalRangedDps),
        ("木工", JobRole.Crafter), ("锻铁", JobRole.Crafter), ("铸甲", JobRole.Crafter), ("雕金", JobRole.Crafter),
        ("制革", JobRole.Crafter), ("裁缝", JobRole.Crafter), ("炼金", JobRole.Crafter), ("烹调", JobRole.Crafter),
        ("采矿", JobRole.Gatherer), ("园艺", JobRole.Gatherer), ("捕鱼", JobRole.Gatherer),
    ];

    private readonly Dictionary<uint, JobInfo> byId = [];
    private readonly List<JobInfo> ordered = [];

    public JobCatalog(IDataManager dataManager, IPluginLog log)
    {
        try
        {
            this.Build(dataManager, log);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[RandomClassPicker] 建立职业目录失败");
        }

        log.Information($"[RandomClassPicker] 职业目录: 共 {this.ordered.Count} 个职业，其中战斗职业 {this.ordered.Count(j => j.IsCombat)} 个");
        foreach (var role in CombatRoles)
        {
            var list = this.ByRole(role).Select(j => j.Name).ToList();
            log.Information($"[RandomClassPicker]   {RoleName(role)}({list.Count}): {string.Join("、", list)}");
        }

        // 没识别出职能的职业单独告警，方便版本更新后补表
        var unknown = this.ordered.Where(j => j.Role == JobRole.Unknown).ToList();
        if (unknown.Count > 0)
        {
            log.Warning($"[RandomClassPicker] 未识别职能的职业（未加入随机池）: " +
                string.Join("、", unknown.Select(j => $"{j.Id}:{j.Name}")));
        }
    }

    /// <summary>按游戏内顺序排列的所有职业。</summary>
    public IReadOnlyList<JobInfo> All => this.ordered;

    public JobInfo? Get(uint id) => this.byId.TryGetValue(id, out var v) ? v : null;

    public string NameOf(uint id) => this.byId.TryGetValue(id, out var v) ? v.Name : $"职业#{id}";

    public JobRole RoleOf(uint id) => this.byId.TryGetValue(id, out var v) ? v.Role : JobRole.Unknown;

    /// <summary>某个职能下的所有战斗职业。</summary>
    public IEnumerable<JobInfo> ByRole(JobRole role) => this.ordered.Where(j => j.Role == role);

    /// <summary>所有战斗职业。</summary>
    public IEnumerable<JobInfo> CombatJobs => this.ordered.Where(j => j.IsCombat);

    private void Build(IDataManager dataManager, IPluginLog log)
    {
        var jobSheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
        if (jobSheet is null)
            return;

        foreach (var row in jobSheet)
        {
            var id = row.RowId;
            var name = row.Name.ExtractText();
            var abbr = row.Abbreviation.ExtractText();

            // 跳过空行和占位行（有些 ID 没有职业）
            if (id == 0 || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(abbr))
                continue;

            var role = LookupRole(id, name);

            var info = new JobInfo(id, name, abbr, role, row.IsLimitedJob);
            this.byId[id] = info;
            this.ordered.Add(info);

            log.Debug($"[RandomClassPicker] 职业 {id} {name}({abbr}) limited={row.IsLimitedJob} => {RoleName(role)}");
        }
    }

    /// <summary>先查 ID 表，查不到再按中文名兜底。</summary>
    private static JobRole LookupRole(uint id, string name)
    {
        if (RoleTable.TryGetValue(id, out var role))
            return role;

        foreach (var (keyword, fallbackRole) in NameFallback)
        {
            if (name.Contains(keyword, StringComparison.Ordinal))
                return fallbackRole;
        }

        return JobRole.Unknown;
    }

    /// <summary>
    /// 职能的中文显示名，使用游戏角色面板里的说法，
    /// 这样界面上的分组和游戏内看到的职能分类完全对得上。
    /// </summary>
    public static string RoleName(JobRole role) => role switch
    {
        JobRole.Tank => "防护职业",
        JobRole.Healer => "治疗职业",
        JobRole.MeleeDps => "近战职业",
        JobRole.PhysicalRangedDps => "远程物理职业",
        JobRole.MagicalRangedDps => "远程魔法职业",
        JobRole.Crafter => "能工巧匠",
        JobRole.Gatherer => "大地使者",
        _ => "其它",
    };

    /// <summary>可以在随机池里出现的职能（本插件永远只随机这些）。</summary>
    public static readonly JobRole[] CombatRoles =
    [
        JobRole.Tank,
        JobRole.Healer,
        JobRole.MeleeDps,
        JobRole.PhysicalRangedDps,
        JobRole.MagicalRangedDps,
    ];
}
