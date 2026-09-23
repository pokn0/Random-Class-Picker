using Dalamud.Configuration;

namespace RandomClassPicker;

/// <summary>随机数来源。</summary>
public enum RandomSource
{
    /// <summary>真随机：random.org 的大气噪声。</summary>
    RandomOrg,

    /// <summary>只要真随机：拿不到就报错，不降级。</summary>
    RandomOrgOnly,

    /// <summary>本地伪随机（System.Random），作为兜底。</summary>
    LocalFallback,
}

/// <summary>一次抽签的职能限定，对应 /rcp tank 这类指令。</summary>
public enum RoleFilter
{
    None,
    Tank,
    Healer,
    MeleeDps,
    PhysicalRangedDps,
    MagicalRangedDps,
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    /// <summary>随机数来源策略。</summary>
    public RandomSource Source { get; set; } = RandomSource.RandomOrg;

    // ------------------------------------------------------------------
    // 随机池筛选
    // 注意：生产和采集职业在代码层面被永久排除，没有任何开关能把它们放进池子。
    // ------------------------------------------------------------------

    /// <summary>
    /// 允许进入随机池的职业白名单。
    /// null 表示"还没配置过"，插件启动时会把所有战斗职业填进去作为默认值。
    /// </summary>
    public List<uint>? EnabledJobIds { get; set; }

    /// <summary>是否排除特殊职业（限定职业：青魔法师、驯兽师）。默认排除。</summary>
    public bool ExcludeSpecialJobs { get; set; } = true;

    // ------------------------------------------------------------------
    // 抽取行为
    // ------------------------------------------------------------------

    /// <summary>
    /// 抽到当前正在使用的那个套装就重抽（只排除这一套，不会排除整个职业）。
    /// 注意早期版本这里排除的是"整个当前职业"，会把同职能的其它套装也一起踢出池子。
    /// </summary>
    public bool ExcludeCurrentGearset { get; set; } = true;

    /// <summary>
    /// 只在品级较高（≥ 100）的套装里抽，用来滤掉过时的旧套装。
    /// 当前版本满级品级约 790，门槛取 100 既能过滤又不会误伤。
    /// </summary>
    public bool OnlyMaxLevelGearsets { get; set; } = false;

    /// <summary>
    /// 同一职业有多个套装时，只保留装备品级最高的那一个。
    /// 默认开启：能避开装备缺件的旧套装（游戏会报"缺少必要的装备"而拒绝切换）。
    /// </summary>
    public bool PreferHighestItemLevelGearset { get; set; } = true;

    /// <summary>抽完立即切换（关闭则只显示结果，由你手动确认）。</summary>
    public bool AutoSwitch { get; set; } = true;

    /// <summary>切换前打印到聊天栏。</summary>
    public bool PrintToChat { get; set; } = true;

    // ------------------------------------------------------------------
    // 界面外快捷入口（服务器信息栏 DTR 图标）
    // ------------------------------------------------------------------

    /// <summary>在服务器信息栏显示快捷图标（点一下即抽即切）。</summary>
    public bool ShowDtrEntry { get; set; } = true;

    /// <summary>快捷图标里是否显示图标符号（关闭则只显示职业名）。</summary>
    public bool ShowDtrIcon { get; set; } = true;

    /// <summary>历史记录（最近 20 次）。</summary>
    public List<string> History { get; set; } = [];

    // ------------------------------------------------------------------
    // 抽取统计
    // ------------------------------------------------------------------

    /// <summary>职业 ID -> 历史被抽中次数。</summary>
    public Dictionary<uint, int> DrawCounts { get; set; } = [];

    /// <summary>累计抽签次数（用于算占比）。</summary>
    public int TotalDraws { get; set; }

    /// <summary>记录一次抽中，返回该职业累计被抽中的次数。</summary>
    public int RecordDraw(uint classJobId)
    {
        this.DrawCounts.TryGetValue(classJobId, out var count);
        count++;
        this.DrawCounts[classJobId] = count;
        this.TotalDraws++;
        return count;
    }

    /// <summary>取某职业的历史被抽中次数。</summary>
    public int DrawCountOf(uint classJobId)
        => this.DrawCounts.TryGetValue(classJobId, out var count) ? count : 0;

    /// <summary>清零统计。</summary>
    public void ResetDrawCounts()
    {
        this.DrawCounts.Clear();
        this.TotalDraws = 0;
    }

    public void AddHistory(string entry)
    {
        this.History.Insert(0, entry);

        // 用游戏内时间做记录，方便回头看
        if (this.History.Count > 20)
            this.History.RemoveRange(20, this.History.Count - 20);
    }

    // ------------------------------------------------------------------
    // 每日随机任务（roulette）
    // ------------------------------------------------------------------

    /// <summary>
    /// 允许参与"随机每日任务"抽选的随机任务 ID 集合。
    /// null 表示还没配置过，插件启动时会把除"导师随机任务"以外的全部填进去。
    /// </summary>
    public List<byte>? SelectedRouletteIds { get; set; }

    /// <summary>在任务搜索器窗口上叠加自绘面板（勾选框 + 抽选按钮）。</summary>
    public bool ShowDutyFinderOverlay { get; set; } = true;

    /// <summary>面板宽度（像素）。</summary>
    public float DutyFinderPanelWidth { get; set; } = 300f;

    /// <summary>面板是否由用户手动摆放（可拖动、位置记住）；关闭则自动贴在任务搜索器左侧。</summary>
    public bool DutyFinderPanelManualPosition { get; set; }

    // ------------------------------------------------------------------
    // 出海垂钓（申请航线菜单）
    // ------------------------------------------------------------------

    /// <summary>在出海垂钓"申请航线"菜单上叠加面板。</summary>
    public bool ShowOceanFishingOverlay { get; set; } = true;

    /// <summary>出海垂钓面板宽度（像素）。</summary>
    public float OceanFishingPanelWidth { get; set; } = 240f;

    /// <summary>
    /// 自动确认"要乘坐 XX 航线吗？"的确认窗口。
    /// 提交流程是两段：先选航线，游戏再弹确认框，需要点「是」才算真正提交。
    /// </summary>
    public bool AutoConfirmOceanRoute { get; set; } = true;

    /// <summary>
    /// 被取消勾选的航线名。
    /// 用"排除列表"而不是"包含列表"，是为了让**默认全选**成立；
    /// 用名字而不是索引作为键，是因为不同时段可选的航线会变（近海/远海组合），索引不稳定。
    /// </summary>
    public List<string> ExcludedOceanRouteNames { get; set; } = [];

    public bool IsOceanRouteSelected(string routeName)
        => !this.ExcludedOceanRouteNames.Contains(routeName);

    public void SetOceanRouteSelected(string routeName, bool selected)
    {
        if (selected)
        {
            this.ExcludedOceanRouteNames.Remove(routeName);
        }
        else if (!this.ExcludedOceanRouteNames.Contains(routeName))
        {
            this.ExcludedOceanRouteNames.Add(routeName);
        }
    }

    public bool IsRouletteSelected(byte id) => this.SelectedRouletteIds?.Contains(id) ?? false;

    public void SetRouletteSelected(byte id, bool selected)
    {
        this.SelectedRouletteIds ??= [];

        if (selected)
        {
            if (!this.SelectedRouletteIds.Contains(id))
                this.SelectedRouletteIds.Add(id);
        }
        else
        {
            this.SelectedRouletteIds.Remove(id);
        }
    }

    // ------------------------------------------------------------------
    // 职业白名单读写
    // ------------------------------------------------------------------

    public bool IsJobEnabled(uint jobId) => this.EnabledJobIds?.Contains(jobId) ?? false;

    public void SetJobEnabled(uint jobId, bool enabled)
    {
        this.EnabledJobIds ??= [];

        if (enabled)
        {
            if (!this.EnabledJobIds.Contains(jobId))
                this.EnabledJobIds.Add(jobId);
        }
        else
        {
            this.EnabledJobIds.Remove(jobId);
        }
    }

    public void SetJobsEnabled(IEnumerable<uint> jobIds, bool enabled)
    {
        foreach (var id in jobIds)
            this.SetJobEnabled(id, enabled);
    }
}
