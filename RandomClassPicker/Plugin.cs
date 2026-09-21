using System.Globalization;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace RandomClassPicker;

/// <summary>
/// 插件入口。
///
/// 职责划分：
///  - 抽签走后台线程（网络 IO 不能卡主线程）
///  - 切换套装的真正调用放回主线程（Framework.Update 里消费"待切换"队列）
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public static string Name => "Random Class Picker";

    private const string CommandName = "/rcp";

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    internal static ICondition Condition { get; private set; } = null!;

    [PluginService]
    internal static IObjectTable ObjectTable { get; private set; } = null!;

    [PluginService]
    internal static IPlayerState PlayerState { get; private set; } = null!;

    [PluginService]
    internal static IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    internal static IChatGui ChatGui { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    internal static IDtrBar DtrBar { get; private set; } = null!;

    [PluginService]
    internal static IGameGui GameGui { get; private set; } = null!;

    private readonly Configuration configuration;
    private readonly RandomOrgClient randomClient;
    private readonly WindowSystem windowSystem;
    private readonly MainWindow window;
    private readonly JobCatalog jobs;
    private readonly DtrBarHandler dtrBar;
    private readonly DutyRouletteCatalog dutyRoulettes;
    private readonly DutyRouletteOverlay dutyOverlay;
    private readonly AddonScanner addonScanner;
    private readonly OceanFishingOverlay oceanOverlay;
    private readonly CancellationTokenSource cancellation = new();

    /// <summary>最近一次抽中的职业名，供 DTR 图标显示。</summary>
    internal string? LastDrawnJobName { get; private set; }

    /// <summary>
    /// 候选池的帧内缓存：Draw 一帧里会多次用到它（总计数、各职能按钮、套装列表），
    /// 每次都要枚举游戏内存，缓存一下避免重复读取。
    /// </summary>
    private List<GearsetInfo>? poolCache;
    private readonly Dictionary<JobRole, List<string>> missingCache = [];

    public Plugin()
    {
        this.configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.jobs = new JobCatalog(DataManager, Log);
        this.InitializeJobSelection();
        this.randomClient = new RandomOrgClient(Log);

        // 关键：Window 只是"能被画的窗口对象"，必须注册到绘制管线里才会真的画出来。
        // 少了这段，IsOpen 无论怎么切界面上都不会有任何反应。
        this.window = new MainWindow(this);
        this.windowSystem = new WindowSystem("RandomClassPicker");
        this.windowSystem.AddWindow(this.window);

        PluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += this.OnOpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += this.OnOpenConfigUi;
        Framework.Update += this.OnFrameworkUpdate;

        CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "随机换职业。用法: /rcp [tank|healer|melee|ranged|caster] | roll | list",
        });

        // 界面外的快捷入口：服务器信息栏图标，点一下即抽即切
        this.dtrBar = new DtrBarHandler(this, Log, DtrBar);

        // 每日随机任务 + 任务搜索器上的叠加面板
        this.dutyRoulettes = new DutyRouletteCatalog(DataManager, Log);
        this.InitializeRouletteSelection();
        this.dutyOverlay = new DutyRouletteOverlay(this, GameGui, Log);

        // 窗口发现工具（用于定位出海垂钓"申请航线"这类没有专用 addon 类型的窗口）
        this.addonScanner = new AddonScanner(Log, GameGui);

        // 出海垂钓"申请航线"菜单上的叠加面板
        this.oceanOverlay = new OceanFishingOverlay(this, GameGui, Log);

        Log.Information($"{Name} 已加载。");

        // 加载时就把读到的套装写进日志，排查"某个职业没进池子"时不必再手动执行指令
        this.LogGearsetSnapshot();
    }

    public void Dispose()
    {
        this.cancellation.Cancel();
        this.cancellation.Dispose();

        Framework.Update -= this.OnFrameworkUpdate;
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= this.OnOpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OnOpenConfigUi;

        this.windowSystem.RemoveAllWindows();
        this.oceanOverlay.Dispose();
        this.dutyOverlay.Dispose();
        this.dtrBar.Dispose();
        this.window.Dispose();
        this.randomClient.Dispose();

        Log.Information($"{Name} 已卸载。");
    }

    internal Configuration Configuration => this.configuration;

    internal RandomOrgClient RandomClient => this.randomClient;

    internal MainWindow Window => this.window;

    internal JobCatalog Jobs => this.jobs;

    internal DtrBarHandler DtrBarHandler => this.dtrBar;

    internal DutyRouletteCatalog DutyRoulettes => this.dutyRoulettes;

    internal DutyRouletteOverlay DutyOverlay => this.dutyOverlay;

    internal AddonScanner AddonScanner => this.addonScanner;

    internal OceanFishingOverlay OceanOverlay => this.oceanOverlay;

    internal void SaveConfig() => PluginInterface.SavePluginConfig(this.configuration);

    /// <summary>
    /// 把诊断信息写到插件自己的目录（不依赖 dalamud.log——那个文件满了就不再写入，
    /// 导致排查时读到的是旧内容）。
    /// </summary>
    private void WriteDiagnostic(string fileName, string content)
    {
        try
        {
            var dir = PluginInterface.ConfigDirectory;
            if (!dir.Exists)
                dir.Create();

            var path = Path.Combine(dir.FullName, fileName);
            File.WriteAllText(path, content, System.Text.Encoding.UTF8);
            Log.Information($"[RandomClassPicker] 诊断已写入 {path}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RandomClassPicker] 写诊断文件失败");
        }
    }

    /// <summary>
    /// 用实际数据自检：随机任务列表顺序、勾选状态、以及初始化时到底勾了哪些。
    /// 输出到插件目录下的 roulette-diagnostic.txt，可反复刷新对比。
    /// </summary>
    internal string BuildRouletteDiagnostic()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"===== 随机任务诊断 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        sb.AppendLine($"插件版本: {typeof(Plugin).Assembly.GetName().Version}");
        sb.AppendLine();

        // 1) 当前扫描结果（顺序 = 面板顺序）；诊断要最新数据，强制刷新
        this.dutyRoulettes.Refresh(force: true);
        sb.AppendLine($"--- 当前扫描结果: {this.dutyRoulettes.Entries.Count} 个（顺序即面板顺序）---");
        var i = 0;
        foreach (var e in this.dutyRoulettes.Entries)
        {
            var sel = this.configuration.IsRouletteSelected(e.Id) ? "已勾选" : "未勾选";
            sb.AppendLine($"{i++,2}. id={e.Id,3} {(e.IsCompleted ? "已完成" : "未完成")} {sel}  「{e.Name}」");
        }

        sb.AppendLine();

        // 2) 配置里保存的勾选列表（原始数据）
        var selected = this.configuration.SelectedRouletteIds;
        sb.AppendLine("--- 配置里保存的 SelectedRouletteIds ---");
        sb.AppendLine(selected is null
            ? "(null，尚未初始化)"
            : $"[{string.Join(", ", selected.OrderBy(x => x))}]  共 {selected.Count} 个");

        sb.AppendLine();

        // 3) 数据集里全部作战随机任务（含被过滤/被排除的），用于核对过滤是否过头
        sb.AppendLine("--- ContentRoulette 表内全部 ContentType=1 条目 ---");
        try
        {
            var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentRoulette>();
            if (sheet is not null)
            {
                foreach (var row in sheet)
                {
                    var name = row.Name.ExtractText();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    if (row.ContentType.RowId != 1)
                        continue;

                    var inList = this.dutyRoulettes.Entries.Any(e => e.Id == row.RowId);
                    var status = inList
                        ? "在列表"
                        : DutyRouletteCatalog.IsExcluded(row.RowId) ? "已排除(不参与随机)" : "被过滤";
                    sb.AppendLine($"id={row.RowId,3} {status}  「{name}」");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"枚举失败: {ex.Message}");
        }

        return sb.ToString();
    }

    /// <summary>取诊断文件路径（给聊天栏提示用）。</summary>
    internal string DiagnosticFilePath => Path.Combine(PluginInterface.ConfigDirectory.FullName, "roulette-diagnostic.txt");

    /// <summary>
    /// 首次运行时，把白名单初始化成"所有战斗职业"。
    /// 生产和采集职业永远不会被填进来（需求 1）。
    /// </summary>
    private void InitializeJobSelection()
    {
        if (this.configuration.EnabledJobIds is not null)
            return;

        this.configuration.EnabledJobIds = this.jobs.CombatJobs
            .Where(j => !j.IsSpecial) // 特殊职业默认也不勾（需求 2 默认排除）
            .Select(j => j.Id)
            .ToList();

        SaveConfig();
        Log.Information($"[RandomClassPicker] 已初始化职业白名单：{this.configuration.EnabledJobIds.Count} 个战斗职业");
    }

    /// <summary>
    /// 组装候选池。所有筛选规则集中在这里，方便核对。
    /// 会在主线程读游戏内存，调用方需保证在主线程。
    /// </summary>
    /// <param name="roleFilter">职能限定。</param>
    /// <param name="applyExcludeCurrent">
    /// 是否应用"抽到当前套装就重抽"。统计可用数量时应传 false——
    /// 当前套装也是可用的，只是抽签时被主动跳过，不该让数量凭空少 1。
    /// </param>
    internal List<GearsetInfo> BuildCandidatePool(RoleFilter roleFilter, bool applyExcludeCurrent = true)
    {
        try
        {
            return this.BuildCandidatePoolCore(roleFilter, applyExcludeCurrent);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[RandomClassPicker] 组装候选池失败");
            return [];
        }
    }

    private List<GearsetInfo> BuildCandidatePoolCore(RoleFilter roleFilter, bool applyExcludeCurrent)
    {
        var onlyMaxLevel = this.configuration.OnlyMaxLevelGearsets;
        var excludeSpecial = this.configuration.ExcludeSpecialJobs;
        var jobs = this.jobs;
        var config = this.configuration;

        // 1) 先用"原始形态"筛选：这些判断只看职业/品级等数值，
        //    不需要解码名字，能省掉每帧为几十个套装分配字符串的开销。
        var raw = EnumerateRawGearsets()
            // 需求 1：生产/采集职业永不在池内（代码层面写死，没有开关能放开）
            .Where(g => jobs.RoleOf(g.ClassJobId) is not (JobRole.Crafter or JobRole.Gatherer))
            // 需求 8：只保留设置面板里勾选的职业
            .Where(g => config.IsJobEnabled(g.ClassJobId))
            // 需求 2：可选排除特殊职业（青魔法师 / 驯兽师）
            .Where(g => !excludeSpecial || jobs.Get(g.ClassJobId)?.IsSpecial != true)
            // 需求 3-7：指令带来的职能限定
            .Where(g => roleFilter == RoleFilter.None || RoleFromFilter(roleFilter) == jobs.RoleOf(g.ClassJobId))
            .Where(g => !onlyMaxLevel || g.ItemLevel >= 100)
            .ToList();

        // 2) 同一职业多个套装时只留品级最高的那个，避免抽到装备缺件的旧套装
        //    （游戏会直接拒绝切换并弹"无法更改套装……缺少必要的装备"）
        if (config.PreferHighestItemLevelGearset)
        {
            raw = raw
                .GroupBy(g => g.ClassJobId)
                .Select(group => group
                    .OrderByDescending(g => g.ItemLevel)
                    .ThenByDescending(g => g.IsCurrent)
                    .ThenBy(g => g.Index)
                    .First())
                .ToList();
        }

        // 3) 只在这时候才解码名字，且只对留下的少数几个套装解码
        var pool = raw
            .Select(r => new GearsetInfo(
                Index: r.Index,
                ClassJobId: r.ClassJobId,
                Name: DecodeGearsetName(r.Name),
                ItemLevel: r.ItemLevel,
                IsCurrent: r.IsCurrent))
            .ToList();

        if (applyExcludeCurrent && config.ExcludeCurrentGearset && pool.Count > 1)
        {
            // 只排除"当前正在穿的那套"，而不是整个职业。
            // 否则当前是坦克时会把全部坦克套装一起踢出池子（表现为坦克可用数凭空少 1）。
            var currentIndex = CurrentGearsetIndex;
            var withoutCurrent = pool.Where(g => g.Index != currentIndex).ToList();
            if (withoutCurrent.Count > 0)
                pool = withoutCurrent;
        }

        return pool;
    }

    /// <summary>
    /// 每个职业只保留装备品级最高的套装；品级相同时优先保留当前正在使用的那套。
    /// </summary>
    internal static List<GearsetInfo> DeduplicateByHighestItemLevel(List<GearsetInfo> gearsets)
    {
        return gearsets
            .GroupBy(g => g.ClassJobId)
            .Select(group => group
                .OrderByDescending(g => g.ItemLevel)
                .ThenByDescending(g => g.IsCurrent)
                // 最后按套装索引稳定排序，保证结果可复现
                .ThenBy(g => g.Index)
                .First())
            .ToList();
    }

    /// <summary>把指令的职能限定映射成职业职能。</summary>
    internal static JobRole RoleFromFilter(RoleFilter filter) => filter switch
    {
        RoleFilter.Tank => JobRole.Tank,
        RoleFilter.Healer => JobRole.Healer,
        RoleFilter.MeleeDps => JobRole.MeleeDps,
        RoleFilter.PhysicalRangedDps => JobRole.PhysicalRangedDps,
        RoleFilter.MagicalRangedDps => JobRole.MagicalRangedDps,
        _ => JobRole.Unknown,
    };

    /// <summary><see cref="RoleFromFilter"/> 的反向映射。</summary>
    internal static RoleFilter RoleFilterFor(JobRole role) => role switch
    {
        JobRole.Tank => RoleFilter.Tank,
        JobRole.Healer => RoleFilter.Healer,
        JobRole.MeleeDps => RoleFilter.MeleeDps,
        JobRole.PhysicalRangedDps => RoleFilter.PhysicalRangedDps,
        JobRole.MagicalRangedDps => RoleFilter.MagicalRangedDps,
        _ => RoleFilter.None,
    };

    /// <summary>
    /// 某个职能下、当前可用的套装（给 /rcp tank 这类指令做前置检查与提示用）。
    /// </summary>
    internal List<GearsetInfo> AvailableGearsetsFor(RoleFilter filter) => this.BuildCandidatePool(filter);

    /// <summary>当前套装索引；不在游戏里时返回 -1。</summary>
    internal static unsafe int CurrentGearsetIndex
    {
        get
        {
            var module = RaptureGearsetModule.Instance();
            return module == null ? -1 : module->CurrentGearsetIndex;
        }
    }

    /// <summary>
    /// 把读到的每个套装写进日志（加载时与 /rcp debug 都会调用）。
    /// 排查"某个职业明明有套装却没进池子"时，这里是第一手证据。
    /// 注意加载瞬间可能还没进游戏世界，读不到套装属正常。
    /// </summary>
    private void LogGearsetSnapshot()
    {
        // 强制取新鲜读数：这是排查用的快照，不能吃缓存
        var gearsets = EnumerateRawGearsetsFresh();
        Log.Information($"[RandomClassPicker] ===== 套装快照: 共读到 {gearsets.Count} 个 =====");

        foreach (var raw in gearsets)
        {
            var jobName = this.jobs.NameOf(raw.ClassJobId);
            var role = JobCatalog.RoleName(this.jobs.RoleOf(raw.ClassJobId));
            var inPool = this.configuration.IsJobEnabled(raw.ClassJobId) ? "在池" : "未勾选";
            Log.Information(
                $"[RandomClassPicker]   套装[{raw.Index}] 职业={jobName}(id{raw.ClassJobId},{role}) " +
                $"名称「{DecodeGearsetName(raw.Name)}」品级={raw.ItemLevel} {inPool}" +
                $"{(raw.IsCurrent ? " [当前]" : string.Empty)}");
        }
    }

    /// <summary>
    /// 解码套装名。
    /// 注意 <c>GearsetEntry.Name</c> 的类型是 <c>Span&lt;byte&gt;</c>（UTF-8 原始字节，非 0 结尾），
    /// 直接 <c>.ToString()</c> 只会得到类型名 "System.Span&lt;Byte&gt;[48]"，必须显式按 UTF-8 解码。
    /// </summary>
    internal static unsafe string DecodeGearsetName(RaptureGearsetModule.GearsetEntry* entry)
        => DecodeGearsetName(entry->Name);

    /// <summary>解码套装名（原始字节形态）。</summary>
    internal static string DecodeGearsetName(byte[] raw) => DecodeGearsetName(raw.AsSpan());

    private static string DecodeGearsetName(Span<byte> raw)
    {
        try
        {
            var span = raw;
            var end = span.IndexOf((byte)0);
            if (end >= 0)
                span = span[..end];

            return span.IsEmpty ? string.Empty : System.Text.Encoding.UTF8.GetString(span);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[RandomClassPicker] 解码套装名失败");
            return string.Empty;
        }
    }

    /// <summary>
    /// 枚举玩家保存的所有有效套装（含解码后的名字）。
    /// 必须在主线程调用。
    /// 只在需要名字时使用；筛选阶段请用 <see cref="EnumerateRawGearsets"/>，
    /// 否则每次都会为全部套装分配字符串。
    /// </summary>
    internal static unsafe List<GearsetInfo> EnumerateGearsets()
    {
        return EnumerateRawGearsetsFresh()
            .Select(r => new GearsetInfo(r.Index, r.ClassJobId, DecodeGearsetName(r.Name), r.ItemLevel, r.IsCurrent))
            .ToList();
    }

    /// <summary>
    /// 套装的轻量形态：名字仍是原始 UTF-8 字节，**不解码成字符串**。
    /// 筛选只看职业/品级等数值，用它可以避免每帧为几十个套装分配字符串。
    /// </summary>
    internal readonly record struct GearsetRaw(
        int Index,
        uint ClassJobId,
        byte[] Name,
        int ItemLevel,
        bool IsCurrent);

    /// <summary>
    /// 枚举所有有效套装的原始形态（不解码名字）。必须在主线程调用。
    /// 带缓存：只按"当前套装索引"失效——切职业/切套装会改变它，因此能覆盖
    /// 界面每帧调用的场景（原先每条槽位每帧都要分配一个字节数组）。
    /// 需要绝对新鲜的读数（例如 /rcp debug、加载时快照）用 <see cref="EnumerateRawGearsetsFresh"/>。
    /// </summary>
    internal static unsafe List<GearsetRaw> EnumerateRawGearsets()
    {
        var module = RaptureGearsetModule.Instance();
        var current = module == null ? -1 : module->CurrentGearsetIndex;

        if (rawCache is not null && rawCacheCurrentIndex == current)
            return rawCache;

        rawCache = EnumerateRawGearsetsFresh();
        rawCacheCurrentIndex = current;
        return rawCache;
    }

    private static List<GearsetRaw>? rawCache;
    private static int rawCacheCurrentIndex = int.MinValue;

    /// <summary>强制重新扫描槽位，绕过缓存。</summary>
    internal static unsafe List<GearsetRaw> EnumerateRawGearsetsFresh()
    {
        var result = new List<GearsetRaw>();
        try
        {
            var module = RaptureGearsetModule.Instance();
            if (module == null)
                return result;

            var current = module->CurrentGearsetIndex;

            // 关键：不能只用 NumGearsets 作上界。
            // 玩家删除套装后编号会出现空洞，且"最高已用索引"未必等于 NumGearsets，
            // 用数量当上界会漏掉编号更大的套装（实测漏掉了蝰蛇剑士的套装）。
            for (var i = 0; i < MaxGearsetSlots; i++)
            {
                if (!module->IsValidGearset(i))
                    continue;

                var entry = module->GetGearset(i);
                if (entry == null)
                    continue;

                result.Add(new GearsetRaw(
                    Index: i,
                    ClassJobId: entry->ClassJob,
                    Name: entry->Name.ToArray(),
                    ItemLevel: entry->ItemLevel,
                    IsCurrent: i == current));
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[RandomClassPicker] 读取套装列表失败");
        }

        return result;
    }

    /// <summary>
    /// 游戏里套装槽位的上限。套装编号是**稀疏**的（玩家删过套装就会留空洞），
    /// 所以不能拿"已保存数量"当循环上界。
    /// </summary>
    private const int MaxGearsetSlots = 200;

    /// <summary>
    /// 抽取并（可选）切换。整个流程在后台线程执行，结果写回窗口。
    /// </summary>
    /// <param name="roleFilter">职能限定，None 表示全职业池。</param>
    internal void RollAndSwitch(RoleFilter roleFilter = RoleFilter.None)
    {
        if (this.window.IsBusy || this.cancellation.IsCancellationRequested)
            return;

        if (!ClientState.IsLoggedIn)
        {
            this.window.SetStatus("尚未进入游戏世界。", isError: true);
            return;
        }

        if (Condition[ConditionFlag.BetweenAreas])
        {
            this.window.SetStatus("正在切换区域/过场中，稍后再试。", isError: true);
            return;
        }

        var roleName = roleFilter == RoleFilter.None ? string.Empty : $"{JobCatalog.RoleName(RoleFromFilter(roleFilter))}职能 · ";
        this.window.IsBusy = true;
        this.window.SetStatus($"正在从 random.org 取真随机数…（{roleName}）", isError: false);

        var source = this.configuration.Source;
        var autoSwitch = this.configuration.AutoSwitch;
        var printToChat = this.configuration.PrintToChat;

        _ = Task.Run(() =>
        {
            try
            {
                if (this.cancellation.IsCancellationRequested)
                    return;

                // 套装信息必须在主线程读，先派回主线程拿快照
                var pool = Framework.RunOnTick(() => this.BuildCandidatePool(roleFilter)).GetAwaiter().GetResult();

                if (pool.Count == 0)
                {
                    var hint = roleFilter == RoleFilter.None
                        ? "没有符合条件的套装。请检查：是否已保存套装、设置里是否勾选了对应职业、是否开启「只抽满级套装」、「同职业只取品级最高的套装」是否把套装都去重掉了。"
                        : $"你没有任何已勾选的{JobCatalog.RoleName(RoleFromFilter(roleFilter))}职业套装（或该职业没在设置里勾选）。";
                    this.window.SetStatus(hint, isError: true);
                    if (printToChat)
                        ChatGui.PrintError($"[随机职业] {hint}");
                    return;
                }

                // 抽索引：1..pool.Count（random.org 区间是闭区间，从 1 开始更直观）
                var draw = this.randomClient.Draw(1, pool.Count, source);
                var picked = pool[draw.Value - 1];
                var jobName = JobNameOf(picked.ClassJobId);
                var roleTag = JobCatalog.RoleName(this.jobs.RoleOf(picked.ClassJobId));

                var summary = $"{jobName}（{roleTag} · 套装「{picked.Name}」 品级{picked.ItemLevel}）";
                var sourceTag = draw.IsTrueRandom ? "真随机·random.org" : "伪随机·本地兜底";

                // 供界面外的 DTR 快捷图标显示
                this.LastDrawnJobName = jobName;

                // 池子里有哪些职业（去重后按池序排列），用于聊天栏说明本次是从什么范围里抽的
                var poolJobNames = pool
                    .Select(g => g.ClassJobId)
                    .Distinct()
                    .Select(JobNameOf)
                    .ToList();

                // 记录并取回该职业的历史被抽中次数
                var drawCount = this.configuration.RecordDraw(picked.ClassJobId);

                this.window.SetResult(new RollResult(
                    JobName: jobName,
                    GearsetName: picked.Name,
                    ItemLevel: picked.ItemLevel,
                    GearsetIndex: picked.Index,
                    ClassJobId: picked.ClassJobId,
                    IsTrueRandom: draw.IsTrueRandom,
                    SourceDetail: draw.Detail,
                    CandidateCount: pool.Count,
                    RoleName: roleTag,
                    PoolJobNames: poolJobNames,
                    JobDrawCount: drawCount));

                this.configuration.AddHistory(
                    $"{DateTime.Now.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)} {summary} [{sourceTag}] 第{drawCount}次");

                // 每次抽签**只落盘一次**：RecordDraw 与 AddHistory 都只改内存，
                // 由这一次 SaveConfig 一起写入（早期版本这里会写两遍，白跑一次完整序列化）。
                SaveConfig();

                if (printToChat)
                {
                    ChatGui.Print(
                        $"[随机职业] 从{poolJobNames.Count}个职业（{string.Join("，", poolJobNames)}）中" +
                        $"抽取到了{jobName}，该职业历史被抽中{drawCount}次 —— {sourceTag}");
                }

                if (autoSwitch)
                {
                    var switched = this.TrySwitchTo(picked.Index);
                    this.window.SetStatus(
                        switched
                            ? $"已切换到 {summary}"
                            : $"抽到 {summary}，但切换被游戏拒绝。如果是「缺少必要的装备」，说明这个套装本身装备缺件，" +
                          "可在「套装 / 历史」里看哪个套装被选中，或依赖「同职业只取品级最高的套装」自动避开。",
                        isError: !switched);
                }
                else
                {
                    this.window.SetStatus($"抽到 {summary}，未自动切换。", isError: false);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[RandomClassPicker] 抽签失败");
                this.window.SetStatus($"抽签失败: {ex.Message}", isError: true);
            }
            finally
            {
                this.window.IsBusy = false;

                // 刷新界面外的快捷图标（显示刚抽中的职业 / 恢复常态）
                this.dtrBar.Refresh();
            }
        });
    }

    /// <summary>
    /// 切换套装。会派回主线程执行，并返回是否调用成功。
    /// </summary>
    internal bool TrySwitchTo(int gearsetIndex)
    {
        if (gearsetIndex < 0)
            return false;

        if (Condition[ConditionFlag.InCombat])
        {
            Log.Warning("[RandomClassPicker] 战斗中无法切换职业");
            return false;
        }

        try
        {
            var result = Framework.RunOnTick(() =>
            {
                unsafe
                {
                    var module = RaptureGearsetModule.Instance();
                    if (module == null || !module->IsValidGearset(gearsetIndex))
                        return false;

                    // EquipGearset 返回 int：0 = 已提交切换，非 0 一般是错误码
                    var ret = module->EquipGearset(gearsetIndex);

                    // 用 Information 级别记录，保证默认日志里能查到（Debug 默认不记录）
                    var entry = module->GetGearset(gearsetIndex);
                    var entryName = entry is null ? "?" : DecodeGearsetName(entry);
                    Log.Information(
                        $"[RandomClassPicker] 请求切换到套装 [{gearsetIndex}]「{entryName}」，" +
                        $"EquipGearset 返回 {ret}，当前套装索引 {CurrentGearsetIndex}");

                    if (ret == 0)
                        return true;

                    return CurrentGearsetIndex == gearsetIndex;
                }
            }).GetAwaiter().GetResult();

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RandomClassPicker] 切换套装失败");
            return false;
        }
    }

    /// <summary>取职业中文名（来自启动时建立的职业目录，即客户端本地化名称）。</summary>
    internal string JobNameOf(uint classJobId) => this.jobs.NameOf(classJobId);

    private void OnOpenMainUi() => this.window.IsOpen = true;

    private void OnOpenConfigUi() => this.window.IsOpen = true;

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            // 无参数：打开窗口（用 IsOpen=true 而不是 Toggle，
            // 否则在窗口默认已打开时第一次输入 /rcp 反而会把它关掉）
            case "":
                this.window.IsOpen = true;
                break;

            case "roll":
            case "r":
                this.window.IsOpen = true;
                this.RollAndSwitch();
                break;

            case "list":
            case "l":
                var gearsets = EnumerateGearsets();
                if (gearsets.Count == 0)
                {
                    ChatGui.Print("[随机职业] 你还没有保存任何套装。");
                    return;
                }

                foreach (var g in gearsets)
                {
                    var marker = g.IsCurrent ? " ← 当前" : string.Empty;
                    var role = JobCatalog.RoleName(this.jobs.RoleOf(g.ClassJobId));
                    var enabled = this.configuration.IsJobEnabled(g.ClassJobId) ? string.Empty : "（未勾选）";
                    ChatGui.Print($"  [{g.Index}] {JobNameOf(g.ClassJobId)}（{role}）「{g.Name}」品级 {g.ItemLevel}{marker}{enabled}");
                }

                break;

            // ---- 按职能随机（需求 3-7）----
            case "tank":
            case "t":
            case "坦克":
                this.RollWithFilter(RoleFilter.Tank);
                break;

            case "healer":
            case "heal":
            case "h":
            case "治疗":
                this.RollWithFilter(RoleFilter.Healer);
                break;

            case "melee":
            case "m":
            case "近战":
                this.RollWithFilter(RoleFilter.MeleeDps);
                break;

            case "ranged":
            case "physranged":
            case "p":
            case "远程":
                this.RollWithFilter(RoleFilter.PhysicalRangedDps);
                break;

            case "caster":
            case "magic":
            case "c":
            case "法系":
                this.RollWithFilter(RoleFilter.MagicalRangedDps);
                break;

            case "help":
            case "?":
                this.window.IsOpen = true;
                ChatGui.Print("[随机职业] /rcp 打开窗口 | /rcp roll 全职业随机 | " +
                    "/rcp tank|healer|melee|ranged|caster 按职能随机 | /rcp daily 打开任务搜索器（随机每日任务面板） | " +
                    "/rcp list 列出套装 | /rcp roulette 列出随机任务 | /rcp scan 记录当前打开的窗口 | " +
                    "/rcp status 查看筛选结果 | /rcp debug 打印职业职能映射");
                break;

            case "status":
            case "s":
                this.PrintStatus();
                break;

            case "daily":
            case "duty":
                this.OpenDutyFinder();
                break;

            case "roulette":
            case "rl":
                this.PrintRouletteList();
                break;

            case "scan":
                this.addonScanner.Start();
                ChatGui.Print("[随机职业] 开始记录当前打开的窗口（3 秒）。请保持目标窗口可见。");
                break;

            case "debug":
            case "d":
                this.PrintJobMapping();
                break;

            default:
                // 未知子命令：给出提示，效果等同 /rcp help
                ChatGui.PrintError($"[随机职业] 未知参数「{args.Trim()}」。");
                ChatGui.Print("[随机职业] /rcp 开关窗口 | /rcp roll 全职业随机 | " +
                    "/rcp tank|healer|melee|ranged|caster 按职能随机 | /rcp list 列出套装 | /rcp status 查看筛选结果");
                break;
        }
    }

    /// <summary>按职能抽签：先给出明确的可用性提示，再走正常流程。</summary>
    private void RollWithFilter(RoleFilter filter)
    {
        var roleName = JobCatalog.RoleName(RoleFromFilter(filter));
        var available = this.AvailableGearsetsFor(filter);

        if (available.Count == 0)
        {
            var msg = $"没有可用的{roleName}职业套装。可能原因：该职能下没有已保存套装，" +
                "或者对应职业在设置面板里没被勾选（特殊职业可能被「排除特殊职业」关掉了）。";
            this.window.SetStatus(msg, isError: true);
            ChatGui.PrintError($"[随机职业] {msg}");
            return;
        }

        this.window.IsOpen = true;
        ChatGui.Print($"[随机职业] {roleName}职能候选 {available.Count} 个套装，开始抽签…");
        this.RollAndSwitch(filter);
    }

    /// <summary>打印当前筛选状态，方便排查"为什么抽不到某个职业"。</summary>
    private void PrintStatus()
    {
        var config = this.configuration;

        var roleSummary = string.Join(
            " / ",
            JobCatalog.CombatRoles.Select(r =>
            {
                var pool = this.BuildCandidatePool(RoleFilter.None).Count(g => this.jobs.RoleOf(g.ClassJobId) == r);
                var enabled = this.jobs.ByRole(r).Count(j => config.IsJobEnabled(j.Id));
                return $"{JobCatalog.RoleName(r)}:{enabled}个已勾选/{pool}个可用套装";
            }));

        ChatGui.Print($"[随机职业] 全职业池可用套装 {this.BuildCandidatePool(RoleFilter.None).Count} 个");
        ChatGui.Print($"[随机职业] {roleSummary}");
        ChatGui.Print($"[随机职业] 排除特殊职业={config.ExcludeSpecialJobs} 排除当前套装={config.ExcludeCurrentGearset} 只抽满级={config.OnlyMaxLevelGearsets} 来源={config.Source}");
        ChatGui.Print("[随机职业] 生产/采集职业已被永久排除。");
    }

    /// <summary>打印"职业 -> 职能"的完整映射，用来核对是否和游戏内分类一致。</summary>
    private void PrintJobMapping()
    {
        // 这份输出同时写日志文件，方便和游戏内角色面板的职能分组逐条对照
        Log.Information("[RandomClassPicker] ===== 职业职能映射 =====");

        foreach (var role in JobCatalog.CombatRoles)
        {
            var debugJobs = this.jobs.ByRole(role).ToList();

            // 与界面按钮共用同一口径，避免两处数字不一致
            var (roleJobCount, roleGearsetCount) = this.RoleStats(RoleFilterFor(role));

            var line = string.Join("、", debugJobs.Select(j => $"{j.Name}(id{j.Id}){(j.IsSpecial ? "(特殊)" : string.Empty)}"));
            Log.Information(
                $"[RandomClassPicker] {JobCatalog.RoleName(role)} 职业共 {debugJobs.Count} 个 / " +
                $"有可用套装 {roleJobCount} 个职业、{roleGearsetCount} 个套装: {line}");

            ChatGui.Print($"[随机职业] {JobCatalog.RoleName(role)}({roleJobCount} 职业可用 / {roleGearsetCount} 套装): " +
                string.Join("、", debugJobs.Select(j => $"{j.Name}{(j.IsSpecial ? "(特殊)" : string.Empty)}")));
        }

        var crafterGatherer = this.jobs.All.Where(j => j.Role is JobRole.Crafter or JobRole.Gatherer).ToList();
        var unknown = this.jobs.All.Where(j => j.Role is JobRole.Unknown).ToList();

        Log.Information($"[RandomClassPicker] 永久排除(生产/采集) {crafterGatherer.Count} 个: " +
            string.Join("、", crafterGatherer.Select(j => j.Name)));
        Log.Information($"[RandomClassPicker] 未识别职能 {unknown.Count} 个: " +
            string.Join("、", unknown.Select(j => $"{j.Name}(id{j.Id})")));

        ChatGui.Print($"[随机职业] 生产/采集已永久排除 {crafterGatherer.Count} 个；未识别职能 {unknown.Count} 个" +
            "（含 ID 的完整映射见 dalamud.log）");

        // 逐职能列出"有职业但没套装"的情况——可用数小于职业总数的唯一原因
        foreach (var role in JobCatalog.CombatRoles)
        {
            var missing = this.JobsMissingGearsets(role);
            if (missing.Count > 0)
                ChatGui.Print($"[随机职业] {JobCatalog.RoleName(role)} 无可用套装: {string.Join("、", missing)}");
        }

        this.LogGearsetSnapshot();

        var gearsets = EnumerateGearsets();
        ChatGui.Print($"[随机职业] 共读到 {gearsets.Count} 个套装（最高编号 {(gearsets.Count > 0 ? gearsets.Max(g => g.Index) : -1)}），明细见 dalamud.log");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // 每帧让界面刷新一次套装快照等缓存
        this.window.Tick();

        // 窗口扫描：结束后把结果写文件并提示
        if (this.addonScanner.IsScanning)
        {
            this.addonScanner.Tick();

            if (!this.addonScanner.IsScanning)
            {
                var result = this.addonScanner.WriteResult();
                this.WriteDiagnostic("addon-scan.txt", result);
                ChatGui.Print($"[随机职业] 窗口扫描完成，结果已写入: " +
                    $"{Path.Combine(PluginInterface.ConfigDirectory.FullName, "addon-scan.txt")}");
                foreach (var line in result.Split('\n').Where(l => l.Contains("size=")).Take(12))
                    ChatGui.Print("  " + line.TrimEnd());
            }
        }
        else
        {
            this.addonScanner.Tick();
        }
    }

    /// <summary>
    /// 初始化/校正随机任务勾选。
    ///
    /// 这里有历史包袱：早期版本的初始化时机不对（列表还没扫出来就写白名单），
    /// 导致个别条目被漏掉、同时把当时还没过滤掉的"陆行鸟竞赛"ID 也写了进去。
    /// 所以现在每次都做一次校正：
    ///   - 丢掉已不在随机任务列表里的 ID（例如陆行鸟竞赛）
    ///   - 丢掉主动排除的 ID（例如行会令）
    ///   - 把列表里存在、但从未被勾选过的 ID 补上（例如曾被漏掉的团队任务）
    /// 用户主动取消过的条目不在这里，不会被强行改回来。
    /// </summary>
    private void InitializeRouletteSelection()
    {
        this.dutyRoulettes.Refresh();

        var validIds = this.dutyRoulettes.Entries.Select(e => e.Id).ToHashSet();

        // 首次运行
        if (this.configuration.SelectedRouletteIds is null)
        {
            this.configuration.SelectedRouletteIds = [.. validIds];
            SaveConfig();
            Log.Information($"[RandomClassPicker] 已初始化随机任务勾选：{validIds.Count} 个");
            return;
        }

        var selected = this.configuration.SelectedRouletteIds;

        // 1) 丢掉无效 ID（陆行鸟竞赛等已不在列表里的）与被主动排除的（行会令）
        var removed = selected.RemoveAll(id => !validIds.Contains(id));

        // 2) 补上缺失的（曾因初始化时机问题被漏掉的）
        var added = 0;
        foreach (var id in validIds)
        {
            if (selected.Contains(id))
                continue;

            selected.Add(id);
            added++;
        }

        if (removed > 0 || added > 0)
        {
            SaveConfig();
            Log.Information(
                $"[RandomClassPicker] 随机任务勾选已校正：移除无效 {removed} 个、补上缺失 {added} 个，" +
                $"现共 {selected.Count} 个");
        }
    }

    /// <summary>
    /// 从"已勾选且未完成"的每日随机任务里随机抽一个并发起参加申请。
    /// 抽选在后台线程（要联网取真随机），排队调用派回主线程执行。
    /// </summary>
    internal void RollDailyRoulette()
    {
        if (this.window.IsBusy || this.cancellation.IsCancellationRequested)
            return;

        if (!ClientState.IsLoggedIn)
        {
            ChatGui.PrintError("[随机职业] 尚未进入游戏世界。");
            return;
        }

        if (Condition[ConditionFlag.InCombat])
        {
            ChatGui.PrintError("[随机职业] 战斗中无法排队。");
            return;
        }

        this.window.IsBusy = true;

        var source = this.configuration.Source;

        _ = Task.Run(() =>
        {
            try
            {
                // 重新读一次完成状态：可能刚打完某个随机任务（强制刷新，不吃节流）
                var pool = Framework.RunOnTick(() =>
                {
                    this.dutyRoulettes.Refresh(force: true);
                    return this.dutyRoulettes.Entries
                        .Where(e => this.configuration.IsRouletteSelected(e.Id))
                        .Where(e => !e.IsCompleted)
                        .ToList();
                }).GetAwaiter().GetResult();

                if (pool.Count == 0)
                {
                    ChatGui.PrintError("[随机职业] 没有可抽的随机任务：请确认已勾选、且勾选的都是未完成状态。");
                    return;
                }

                var draw = this.randomClient.Draw(1, pool.Count, source);
                var picked = pool[draw.Value - 1];
                var sourceTag = draw.IsTrueRandom ? "真随机·random.org" : "伪随机·本地兜底";

                var poolNames = string.Join("，", pool.Select(e => e.Name));

                // 排队动作必须在主线程
                var queued = Framework.RunOnTick(() =>
                {
                    unsafe
                    {
                        var finder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder.Instance();
                        if (finder == null)
                            return false;

                        var queue = finder->GetQueueInfo();
                        if (queue == null)
                            return false;

                        queue->QueueRoulette(picked.Id);
                        return true;
                    }
                }).GetAwaiter().GetResult();

                this.dutyOverlay.SetLastDrawn(picked.Name);

                if (queued)
                {
                    ChatGui.Print(
                        $"[随机职业] 从{pool.Count}个未完成的随机任务（{poolNames}）中抽取到了{picked.Name}，" +
                        $"已发起参加申请 —— {sourceTag}");
                }
                else
                {
                    ChatGui.PrintError($"[随机职业] 抽到了{picked.Name}，但发起排队失败（队列对象不可用）。");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[RandomClassPicker] 随机每日任务抽签失败");
                ChatGui.PrintError($"[随机职业] 随机每日任务抽签失败: {ex.Message}");
            }
            finally
            {
                this.window.IsBusy = false;
                this.dtrBar.Refresh();
            }
        });
    }

    /// <summary>供界面调用：把随机任务勾选恢复成"全部选中"。</summary>
    internal void ResetRouletteSelection()
    {
        this.dutyRoulettes.Refresh(force: true);
        this.configuration.SelectedRouletteIds = this.dutyRoulettes.Entries.Select(e => e.Id).ToList();
        SaveConfig();
        ChatGui.Print($"[随机职业] 随机任务勾选已重置为全部选中（{this.configuration.SelectedRouletteIds.Count} 个）。");
    }

    /// <summary>供界面按钮调用。</summary>
    internal void OpenDutyFinderFromUi() => this.OpenDutyFinder();

    /// <summary>
    /// 打印全部随机任务（ID / 名称 / 完成状态 / 是否勾选），并写诊断文件。
    /// 用来核对面板顺序与游戏内一致，也方便排查某个条目为什么没出现。
    /// </summary>
    private void PrintRouletteList()
    {
        var diagnostic = this.BuildRouletteDiagnostic();
        this.WriteDiagnostic("roulette-diagnostic.txt", diagnostic);

        var entries = this.dutyRoulettes.Entries;

        ChatGui.Print($"[随机职业] 随机任务共 {entries.Count} 个（顺序与面板一致）：");
        foreach (var e in entries)
        {
            var state = e.IsCompleted ? "已完成" : "未完成";
            var selected = this.configuration.IsRouletteSelected(e.Id) ? "√" : "×";
            ChatGui.Print($"  [{e.Id}] {selected} {e.Name}（{state}）");
        }

        ChatGui.Print($"[随机职业] 完整诊断（含被过滤的条目）已写入: {this.DiagnosticFilePath}");
    }

    /// <summary>打开任务搜索器（叠加面板会在它上面显示），并刷新随机任务完成状态。</summary>
    private void OpenDutyFinder()
    {
        try
        {
            this.dutyRoulettes.Refresh();

            unsafe
            {
                var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentContentsFinder.Instance();
                if (agent == null)
                {
                    ChatGui.PrintError("[随机职业] 任务搜索器不可用（可能尚未进入游戏世界）。");
                    return;
                }

                agent->Show();
            }
            var unfinished = this.dutyRoulettes.Entries
                .Where(e => this.configuration.IsRouletteSelected(e.Id) && !e.IsCompleted)
                .Select(e => e.Name)
                .ToList();

            ChatGui.Print(
                $"[随机职业] 已打开任务搜索器，面板显示在窗口右侧。当前可抽的未完成随机任务 {unfinished.Count} 个" +
                (unfinished.Count > 0 ? $": {string.Join("、", unfinished)}" : "（请先在面板里勾选）"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RandomClassPicker] 打开任务搜索器失败");
            ChatGui.PrintError($"[随机职业] 打开任务搜索器失败: {ex.Message}");
        }
    }

    /// <summary>返回当前队列状态的中文描述；未排队时返回空串。</summary>
    internal string CurrentQueueDescription()
    {
        try
        {
            unsafe
            {
                var finder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder.Instance();
                if (finder == null)
                    return string.Empty;

                var queue = finder->GetQueueInfo();
                if (queue == null)
                    return string.Empty;

                // 用数值判断而不是引用枚举：该枚举符号在编译期解析不到
                // （命名空间层级与嵌套层级都试过），而 None = 0、其余均为"已进入排队流程"。
                var state = (byte)queue->QueueState;
                if (state == 0)
                    return string.Empty;

                return state switch
                {
                    1 => "正在提交排队请求…",
                    2 => "排队中…",
                    3 => "已就绪，等待确认进入",
                    4 => "已接受，即将进入副本",
                    5 => "已在副本中",
                    _ => $"队列状态: {state}",
                };
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 该职能下"在池子里但它没有可用套装"的职业名。用来解释为什么可用职业数可能小于职业总数。
    /// 结果按帧缓存（界面一帧里会问好几次）。
    /// </summary>
    internal List<string> JobsMissingGearsets(JobRole role)
    {
        if (this.missingCache.TryGetValue(role, out var cached))
            return cached;

        var poolJobIds = this.BuildCandidatePool(RoleFilterFor(role), applyExcludeCurrent: false)
            .Select(g => g.ClassJobId)
            .ToHashSet();

        var missing = this.jobs.ByRole(role)
            .Where(j => !poolJobIds.Contains(j.Id))
            .Select(j => j.Name)
            .ToList();

        this.missingCache[role] = missing;
        return missing;
    }

    /// <summary>
    /// 一次算出全部战斗职能的统计，供界面按钮使用。
    /// 只建一次池子，避免每个按钮各建一次（原先 5 个按钮 × 每帧 = 5 次全量枚举）。
    /// </summary>
    internal Dictionary<RoleFilter, (int JobCount, int GearsetCount)> AllRoleStats()
    {
        // 整个池子只算一次，再按职能分组统计
        var pool = this.GetPoolCached(RoleFilter.None);

        var jobCounts = new Dictionary<JobRole, HashSet<uint>>();
        var gearsetCounts = new Dictionary<JobRole, int>();

        foreach (var g in pool)
        {
            var role = this.jobs.RoleOf(g.ClassJobId);

            if (!jobCounts.TryGetValue(role, out var set))
            {
                set = [];
                jobCounts[role] = set;
            }

            set.Add(g.ClassJobId);
            gearsetCounts[role] = gearsetCounts.GetValueOrDefault(role) + 1;
        }

        var result = new Dictionary<RoleFilter, (int, int)>();
        foreach (var role in JobCatalog.CombatRoles)
        {
            result[RoleFilterFor(role)] = (
                jobCounts.GetValueOrDefault(role)?.Count ?? 0,
                gearsetCounts.GetValueOrDefault(role));
        }

        return result;
    }

    /// <summary>
    /// 某个职能的候选统计。界面按钮与 /rcp debug 共用这一份口径，保证数字一致。
    /// 刻意**不**应用"排除当前套装"：当前套装也是可用的，只是抽签时被主动跳过，
    /// 不该让显示的数量凭空少 1（曾因此让近战显示成 5 而实际有 6）。
    /// </summary>
    /// <param name="jobCount">该职能下有可用套装的职业数。</param>
    /// <param name="gearsetCount">该职能下可用套装总数。</param>
    internal (int JobCount, int GearsetCount) RoleStats(RoleFilter filter)
    {
        var pool = this.BuildCandidatePool(filter, applyExcludeCurrent: false);
        return (pool.Select(g => g.ClassJobId).Distinct().Count(), pool.Count);
    }

    /// <summary>当前正在穿的套装是否会被"排除当前套装"跳过（用于界面提示）。</summary>
    internal bool CurrentGearsetWillBeSkipped(RoleFilter filter)
    {
        if (!this.configuration.ExcludeCurrentGearset)
            return false;

        var currentIndex = CurrentGearsetIndex;
        if (currentIndex < 0)
            return false;

        var pool = this.BuildCandidatePool(filter, applyExcludeCurrent: false);
        return pool.Count > 1 && pool.Any(g => g.Index == currentIndex);
    }

    /// <summary>
    /// 取本帧的候选池（带帧内缓存）。只在主线程调用。
    /// 这里返回的是**可用**口径（不应用"排除当前套装"）。
    /// </summary>
    internal List<GearsetInfo> GetPoolCached(RoleFilter roleFilter)
    {
        if (this.poolCache is not null && roleFilter == RoleFilter.None)
            return this.poolCache;

        var pool = this.BuildCandidatePool(roleFilter, applyExcludeCurrent: false);

        if (roleFilter == RoleFilter.None)
            this.poolCache = pool;

        return pool;
    }

    /// <summary>界面每帧开始绘制时调用，用来作废上一帧的缓存。</summary>
    internal void BeginUiFrame()
    {
        this.poolCache = null;
        this.missingCache.Clear();
    }
}

/// <summary>关于一个套装的信息快照。</summary>
public readonly record struct GearsetInfo(
    int Index,
    uint ClassJobId,
    string Name,
    int ItemLevel,
    bool IsCurrent);

/// <summary>一次抽签的展示数据。</summary>
public readonly record struct RollResult(
    string JobName,
    string GearsetName,
    int ItemLevel,
    int GearsetIndex,
    uint ClassJobId,
    bool IsTrueRandom,
    string SourceDetail,
    int CandidateCount,
    string RoleName,
    List<string> PoolJobNames,
    int JobDrawCount);
