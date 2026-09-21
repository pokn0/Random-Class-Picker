using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RandomClassPicker;

/// <summary>
/// 主界面 + 设置面板。
/// 所有 Draw* 都在渲染线程执行，不要在这里做网络/磁盘 IO。
/// 抽签结果由后台线程写入字段，Draw 只负责读取展示。
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private const string WindowId = "随机换职业###RandomClassPickerMain";

    private readonly Plugin plugin;
    private readonly List<GearsetInfo> preview = [];

    private string status = "点下面的按钮开始抽签。";
    private bool statusIsError;
    private RollResult? lastResult;
    private int previewFrame;
    private bool statsOnlyDrawn;

    public MainWindow(Plugin plugin)
        : base(WindowId, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        // 加载后直接可见，避免"插件启用了却什么都没出现"
        this.IsOpen = true;
    }

    internal bool IsBusy { get; set; }

    /// <summary>最近一次抽签结果（供界面外的快捷图标显示）。</summary>
    internal RollResult? LastResult => this.lastResult;

    internal void SetStatus(string text, bool isError)
    {
        this.status = text;
        this.statusIsError = isError;
    }

    internal void SetResult(RollResult result) => this.lastResult = result;

    /// <summary>每帧由 Framework.Update 调用（主线程）。</summary>
    public void Tick()
    {
        // 套装列表不需要每帧读，隔几帧刷新一次即可
        this.previewFrame++;
        if (this.previewFrame % 30 != 0)
            return;

        this.preview.Clear();
        this.preview.AddRange(Plugin.EnumerateGearsets());
    }

    public override void Draw()
    {
        // 作废上一帧的候选池缓存：这一帧里多处要用到池子，只真正算一次
        this.plugin.BeginUiFrame();

        var config = this.plugin.Configuration;

        if (ImGui.BeginTabBar("##rcpTabs"))
        {
            if (ImGui.BeginTabItem("抽签"))
            {
                this.DrawRollTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("设置"))
            {
                this.DrawSettingsTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("职业池"))
            {
                this.DrawJobPoolTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("套装 / 历史"))
            {
                this.DrawGearsetTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("抽中统计"))
            {
                this.DrawStatsTab(config);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        // ---------- 状态栏 ----------
        ImGui.Separator();
        ImGui.TextColored(
            this.statusIsError ? new Vector4(1f, 0.4f, 0.4f, 1f) : new Vector4(0.7f, 0.85f, 1f, 1f),
            this.status);
    }

    // ==================================================================
    // 抽签
    // ==================================================================
    private void DrawRollTab(Configuration config)
    {
        var currentIndex = Plugin.CurrentGearsetIndex;

        if (!Plugin.ClientState.IsLoggedIn)
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), "尚未进入游戏世界");
        }
        else
        {
            var currentJobId = Plugin.PlayerState.ClassJob.RowId;
            var currentJob = currentJobId != 0 ? this.plugin.JobNameOf(currentJobId) : "未知";
            var currentRole = JobCatalog.RoleName(this.plugin.Jobs.RoleOf(currentJobId));
            ImGui.TextUnformatted($"当前职业: {currentJob}（{currentRole}） Lv.{Plugin.PlayerState.Level}");
            ImGui.TextUnformatted($"当前套装: [{currentIndex}]   已保存套装: {this.preview.Count} 个");
        }

        var poolCount = this.plugin.GetPoolCached(RoleFilter.None).Count;
        ImGui.TextDisabled($"当前设置下可用候选: {poolCount} 个套装");
        if (this.plugin.CurrentGearsetWillBeSkipped(RoleFilter.None))
        {
            ImGui.SameLine();
            ImGui.TextColored(
                new Vector4(0.6f, 0.8f, 1f, 1f),
                "（抽签时其中「当前套装」会被跳过，不影响上面的可用数）");
        }

        if (poolCount == 0)
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), "候选为空，请到「职业池」标签页勾选职业。");

        ImGui.Separator();

        ImGui.BeginDisabled(this.IsBusy || !Plugin.ClientState.IsLoggedIn);
        if (ImGui.Button(this.IsBusy ? "抽取中…" : "🎲 随机换一个职业", new Vector2(-1, 42)))
            this.plugin.RollAndSwitch();
        ImGui.EndDisabled();

        // ---- 按职能快捷抽签 ----
        ImGui.Spacing();
        ImGui.TextDisabled("按职能抽签（等价于 /rcp tank 等指令）：");

        var roleButtons = new (string Label, RoleFilter Filter, string Command)[]
        {
            ("坦克", RoleFilter.Tank, "/rcp tank"),
            ("治疗", RoleFilter.Healer, "/rcp healer"),
            ("近战", RoleFilter.MeleeDps, "/rcp melee"),
            ("远程物理", RoleFilter.PhysicalRangedDps, "/rcp ranged"),
            ("法系", RoleFilter.MagicalRangedDps, "/rcp caster"),
        };

        // 一次算出所有职能的统计，避免 5 个按钮各建一次池子
        var allRoleStats = this.plugin.AllRoleStats();

        ImGui.BeginDisabled(this.IsBusy || !Plugin.ClientState.IsLoggedIn);
        for (var i = 0; i < roleButtons.Length; i++)
        {
            var (label, filter, command) = roleButtons[i];
            var (jobCount, gearsetCount) = allRoleStats.GetValueOrDefault(filter);

            if (i > 0)
                ImGui.SameLine();

            ImGui.BeginDisabled(jobCount == 0);
            if (ImGui.Button($"{label} ({jobCount}/{gearsetCount})"))
                this.plugin.RollAndSwitch(filter);
            ImGui.EndDisabled();

            if (ImGui.IsItemHovered())
            {
                var missing = this.plugin.JobsMissingGearsets(Plugin.RoleFromFilter(filter));
                ImGui.SetTooltip(
                    $"{command}\n" +
                    $"该职能下可用职业: {jobCount} 个\n" +
                    $"可用套装: {gearsetCount} 个\n" +
                    (missing.Count > 0
                        ? $"无可用套装的职业（未计入）: {string.Join("、", missing)}"
                        : "该职能下所有职业都有可用套装") +
                    "\n（数字与 /rcp debug 一致）");
            }
        }

        ImGui.EndDisabled();

        // 把"为什么某个职业没进池子"直接摊开讲，省得靠猜
        var allMissing = new List<string>();
        foreach (var (label, filter, _) in roleButtons)
        {
            var missing = this.plugin.JobsMissingGearsets(Plugin.RoleFromFilter(filter));
            if (missing.Count > 0)
                allMissing.Add($"{label}: {string.Join("、", missing)}");
        }

        if (allMissing.Count > 0)
        {
            ImGui.TextDisabled("无可用套装的职业（故未计入上面的数字）—— " + string.Join("；", allMissing));
        }

        // ---- 结果 ----
        if (this.lastResult is { } r)
        {
            ImGui.Spacing();
            ImGui.Separator();
            var color = r.IsTrueRandom
                ? new Vector4(0.4f, 1f, 0.4f, 1f)
                : new Vector4(1f, 0.7f, 0.2f, 1f);

            ImGui.TextColored(color, r.IsTrueRandom ? "✦ 真随机" : "△ 伪随机兜底");
            ImGui.SameLine();
            ImGui.TextDisabled($"({r.SourceDetail})");

            ImGui.TextUnformatted($"抽到: {r.JobName}（{r.RoleName}）");
            ImGui.TextUnformatted($"套装: 「{r.GearsetName}」 品级 {r.ItemLevel} (索引 {r.GearsetIndex})");
            ImGui.TextDisabled($"从 {r.PoolJobNames.Count} 个职业（{string.Join("，", r.PoolJobNames)}）中抽出（池中套装 {r.CandidateCount} 个）");
            ImGui.TextColored(
                new Vector4(0.7f, 0.85f, 1f, 1f),
                $"{r.JobName} 历史被抽中 {r.JobDrawCount} 次");

            ImGui.Spacing();
            ImGui.BeginDisabled(this.IsBusy);
            if (ImGui.Button("再抽一次"))
                this.plugin.RollAndSwitch();

            ImGui.SameLine();
            if (ImGui.Button("手动切换到它"))
            {
                var ok = this.plugin.TrySwitchTo(r.GearsetIndex);
                this.SetStatus(ok ? $"已切换到 {r.JobName}。" : "切换被拒绝（战斗中/过场中？）。", !ok);
            }

            ImGui.EndDisabled();
        }
    }

    // ==================================================================
    // 设置
    // ==================================================================
    private void DrawSettingsTab(Configuration config)
    {
        var changed = false;

        ImGui.TextUnformatted("随机数来源");
        var sourceIndex = (int)config.Source;
        if (ImGui.RadioButton("random.org 真随机（失败时降级）", ref sourceIndex, (int)RandomSource.RandomOrg))
            changed = true;
        if (ImGui.RadioButton("只用 random.org（失败就报错，不降级）", ref sourceIndex, (int)RandomSource.RandomOrgOnly))
            changed = true;
        if (ImGui.RadioButton("只用本地伪随机（不联网）", ref sourceIndex, (int)RandomSource.LocalFallback))
            changed = true;

        if (changed)
            config.Source = (RandomSource)sourceIndex;

        if (ImGui.Button("测试 random.org 连接"))
        {
            this.SetStatus("正在测试…", false);
            _ = Task.Run(() =>
            {
                try
                {
                    this.SetStatus(this.plugin.RandomClient.TestConnection(), false);
                }
                catch (Exception ex)
                {
                    this.SetStatus($"测试失败: {ex.Message}", true);
                }
            });
        }

        ImGui.Separator();

        // ---- 随机池筛选 ----
        ImGui.TextUnformatted("随机池筛选");

        ImGui.TextColored(
            new Vector4(0.6f, 0.8f, 1f, 1f),
            "生产和采集职业已被永久排除，无法加入随机池。");

        var excludeSpecial = config.ExcludeSpecialJobs;
        if (ImGui.Checkbox("排除特殊职业（青魔法师、驯兽师）", ref excludeSpecial))
        {
            config.ExcludeSpecialJobs = excludeSpecial;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("特殊职业 = 限定职业（Limited Job）。\n勾选后即使它们在职业池里打勾，也不会进入随机池。");

        var excludeCurrent = config.ExcludeCurrentGearset;
        if (ImGui.Checkbox("抽到当前套装就重抽", ref excludeCurrent))
        {
            config.ExcludeCurrentGearset = excludeCurrent;
            changed = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "只排除你当前正在穿的那一套，不会排除整个职业。\n" +
                "（例如当前是坦克，其它坦克套装依然会被抽到。）");
        }

        var onlyMaxLevel = config.OnlyMaxLevelGearsets;
        if (ImGui.Checkbox("只抽满级套装（品级 ≥ 100）", ref onlyMaxLevel))
        {
            config.OnlyMaxLevelGearsets = onlyMaxLevel;
            changed = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "过滤掉品级过低的旧套装。\n" +
                "门槛固定为 100：当前版本满级品级约 790，\n" +
                "100 足以滤掉过时套装又不会误伤任何现役套装。");
        }

        var preferHighest = config.PreferHighestItemLevelGearset;
        if (ImGui.Checkbox("同职业只取品级最高的套装", ref preferHighest))
        {
            config.PreferHighestItemLevelGearset = preferHighest;
            changed = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "同一职业有多个套装时，只把装备品级最高的那个放进随机池。\n" +
                "可以避免抽到装备缺件的旧套装——那种套装游戏会拒绝切换，\n" +
                "并弹出「无法更改套装……缺少必要的装备」。");
        }

        ImGui.Separator();

        // ---- 抽取行为 ----
        ImGui.TextUnformatted("抽取行为");

        var autoSwitch = config.AutoSwitch;
        if (ImGui.Checkbox("抽完立即切换", ref autoSwitch))
        {
            config.AutoSwitch = autoSwitch;
            changed = true;
        }

        var printToChat = config.PrintToChat;
        if (ImGui.Checkbox("抽签结果打印到聊天栏", ref printToChat))
        {
            config.PrintToChat = printToChat;
            changed = true;
        }

        ImGui.Separator();

        // ---- 界面外快捷入口 ----
        ImGui.TextUnformatted("界面外快捷入口（服务器信息栏图标）");

        var showDtr = config.ShowDtrEntry;
        if (ImGui.Checkbox("在服务器信息栏显示快捷图标", ref showDtr))
        {
            config.ShowDtrEntry = showDtr;
            changed = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "在界面顶端的服务器信息栏常驻一个小图标，\n" +
                "左键点击 = 直接随机抽一个职业并切换（不用开窗口、不用输指令）。\n" +
                "按住 Ctrl 点击 = 打开本窗口。\n" +
                "看不到的话：游戏里解锁 HUD 布局，把该条目从隐藏列表调出来。");
        }

        var showDtrIcon = config.ShowDtrIcon;
        if (ImGui.Checkbox("图标里显示符号（关闭则只显示职业名）", ref showDtrIcon))
        {
            config.ShowDtrIcon = showDtrIcon;
            changed = true;
        }

        if (ImGui.Button("立即刷新快捷图标"))
            this.plugin.DtrBarHandler.Refresh();

        ImGui.Separator();

        // ---- 随机每日任务 ----
        ImGui.TextUnformatted("随机每日任务（任务搜索器面板）");

        var showOverlay = config.ShowDutyFinderOverlay;
        if (ImGui.Checkbox("在任务搜索器窗口上显示面板", ref showOverlay))
        {
            config.ShowDutyFinderOverlay = showOverlay;
            changed = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "打开游戏的任务搜索器时，在窗口右侧叠加一个自绘面板：\n" +
                "每个每日随机任务一个复选框 + 一个「随机抽一个并排队」按钮。\n" +
                "抽选只从已勾选且未完成的任务里抽。");
        }

        if (ImGui.Button("打开任务搜索器（/rcp daily）"))
            this.plugin.OpenDutyFinderFromUi();

        var panelWidth = config.DutyFinderPanelWidth;
        if (ImGui.SliderFloat("面板宽度", ref panelWidth, 200f, 600f, "%.0f px"))
        {
            config.DutyFinderPanelWidth = panelWidth;
            changed = true;
        }

        var manualPos = config.DutyFinderPanelManualPosition;
        if (ImGui.Checkbox("面板位置手动摆放（可拖动，位置记住）", ref manualPos))
        {
            config.DutyFinderPanelManualPosition = manualPos;
            this.plugin.DutyOverlay.ResetManualPosition();
            changed = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "关闭时：面板自动贴在任务搜索器左侧（跟随窗口）。\n" +
                "开启时：面板可以拖到自己喜欢的位置，位置会记住。");
        }

        ImGui.Separator();

        // ---- 出海垂钓 ----
        ImGui.TextUnformatted("出海垂钓（申请航线菜单）");

        var showOcean = config.ShowOceanFishingOverlay;
        if (ImGui.Checkbox("在申请航线菜单上显示面板", ref showOcean))
        {
            config.ShowOceanFishingOverlay = showOcean;
            changed = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "跟 NPC 对话打开「申请航线」菜单时，在菜单旁叠加一个自绘面板：\n" +
                "近海/远海逐条勾选 + 一个「随机抽一条并申请」按钮。\n" +
                "抽中后直接提交申请，等价于在菜单里点该航线。");
        }

        var oceanWidth = config.OceanFishingPanelWidth;
        if (ImGui.SliderFloat("出海面板宽度", ref oceanWidth, 180f, 500f, "%.0f px"))
        {
            config.OceanFishingPanelWidth = oceanWidth;
            changed = true;
        }

        if (changed)
            this.plugin.SaveConfig();

        if (changed)
            this.plugin.DtrBarHandler.Refresh();
    }

    // ==================================================================
    // 职业池
    // ==================================================================
    private void DrawJobPoolTab(Configuration config)
    {
        var jobs = this.plugin.Jobs;
        var enabledCount = jobs.All.Count(j => config.IsJobEnabled(j.Id));

        ImGui.TextUnformatted($"已勾选职业: {enabledCount} / {jobs.All.Count}");
        ImGui.TextDisabled("只有打勾的职业会进入随机池。生产/采集职业不在此列表中。");
        ImGui.Separator();

        // ---- 快捷操作 ----
        if (ImGui.Button("全选战斗职业"))
            this.ApplyJobSelection(config, jobs.CombatJobs, true);

        ImGui.SameLine();
        if (ImGui.Button("全部取消"))
            this.ApplyJobSelection(config, jobs.All, false);

        ImGui.SameLine();
        if (ImGui.Button("取消所有特殊职业"))
            this.ApplyJobSelection(config, jobs.All.Where(j => j.IsSpecial), false);

        // ---- 按职能批量勾选 ----
        ImGui.Spacing();
        ImGui.TextDisabled("按职能批量操作（点击 = 全选/全不选该职能）：");

        for (var i = 0; i < JobCatalog.CombatRoles.Length; i++)
        {
            var role = JobCatalog.CombatRoles[i];
            var roleJobs = jobs.ByRole(role).ToList();
            if (roleJobs.Count == 0)
                continue;

            if (i > 0)
                ImGui.SameLine();

            var enabledInRole = roleJobs.Count(j => config.IsJobEnabled(j.Id));
            if (ImGui.Button($"{JobCatalog.RoleName(role)} {enabledInRole}/{roleJobs.Count}##role{role}"))
                this.ApplyJobSelection(config, roleJobs, enabledInRole < roleJobs.Count);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"点击：全选/全不选该职能\n对应指令: {CommandForRole(role)}");
        }

        ImGui.Separator();

        // ---- 逐个职业勾选 ----
        if (ImGui.BeginChild("##jobList", new Vector2(0, -1), true))
        {
            var jobsChanged = false;

            foreach (var role in JobCatalog.CombatRoles)
            {
                var roleJobs = jobs.ByRole(role).ToList();
                if (roleJobs.Count == 0)
                    continue;

                var (roleJobCount, roleGearsetCount) = this.plugin.RoleStats(Plugin.RoleFilterFor(role));
                // 标题同时给出"职业总数"和"当前可用"，避免和按钮上的数字混淆
                if (!ImGui.CollapsingHeader(
                        $"{JobCatalog.RoleName(role)}（共 {roleJobs.Count} 职业 / 可用 {roleJobCount} 职业 · {roleGearsetCount} 套装）##hdr{role}",
                        ImGuiTreeNodeFlags.DefaultOpen))
                {
                    continue;
                }

                foreach (var job in roleJobs)
                {
                    var enabled = config.IsJobEnabled(job.Id);
                    if (ImGui.Checkbox($"{job.Name}##job{job.Id}", ref enabled))
                    {
                        config.SetJobEnabled(job.Id, enabled);
                        jobsChanged = true;
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(
                            $"ID {job.Id} / {job.Abbreviation}\n" +
                            $"职能: {JobCatalog.RoleName(job.Role)}" +
                            (job.IsSpecial ? "\n特殊职业（限定职业）" : string.Empty));
                    }
                }
            }

            // 特殊职业单独一组，方便一眼看到
            var specials = jobs.All.Where(j => j.IsSpecial).ToList();
            if (specials.Count > 0 &&
                ImGui.CollapsingHeader($"特殊职业（{specials.Count}）##hdrSpecial", ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (var job in specials)
                {
                    var enabled = config.IsJobEnabled(job.Id);
                    if (ImGui.Checkbox($"{job.Name}（特殊）##job{job.Id}", ref enabled))
                    {
                        config.SetJobEnabled(job.Id, enabled);
                        jobsChanged = true;
                    }
                }

                ImGui.TextColored(
                    new Vector4(1f, 0.7f, 0.2f, 1f),
                    config.ExcludeSpecialJobs
                        ? "提示：「排除特殊职业」当前为开启状态，上面打勾不会生效。"
                        : "提示：「排除特殊职业」已关闭，打勾的特殊职业会进入随机池。");
            }

            if (jobsChanged)
                this.plugin.SaveConfig();

            ImGui.EndChild();
        }
    }

    private void ApplyJobSelection(Configuration config, IEnumerable<JobInfo> jobs, bool enabled)
    {
        config.SetJobsEnabled(jobs.Select(j => j.Id), enabled);
        this.plugin.SaveConfig();
    }

    private static string CommandForRole(JobRole role) => role switch
    {
        JobRole.Tank => "/rcp tank",
        JobRole.Healer => "/rcp healer",
        JobRole.MeleeDps => "/rcp melee",
        JobRole.PhysicalRangedDps => "/rcp ranged",
        JobRole.MagicalRangedDps => "/rcp caster",
        _ => "（无）",
    };

    // ==================================================================
    // 套装 / 历史
    // ==================================================================
    private void DrawGearsetTab(Configuration config)
    {
        var inPool = this.plugin.GetPoolCached(RoleFilter.None);
        var inPoolIndexes = inPool.Select(g => g.Index).ToHashSet();

        // 同职业里被"取品级最高"规则淘汰掉的套装，界面上标出来，便于理解为什么它不在池里
        var superseded = new HashSet<int>();
        if (config.PreferHighestItemLevelGearset && this.preview.Count > 1)
        {
            var kept = Plugin.DeduplicateByHighestItemLevel([.. this.preview]).Select(g => g.Index).ToHashSet();
            foreach (var g in this.preview.Where(g => !kept.Contains(g.Index)))
                superseded.Add(g.Index);
        }
        if (ImGui.CollapsingHeader($"我的套装 ({this.preview.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (this.preview.Count == 0)
            {
                ImGui.TextDisabled("没有读到套装。请先在游戏里保存至少一个套装。");
            }
            else
            {
                foreach (var g in this.preview)
                {
                    var role = JobCatalog.RoleName(this.plugin.Jobs.RoleOf(g.ClassJobId));
                    var label = $"[{g.Index}] {this.plugin.JobNameOf(g.ClassJobId)}（{role}）「{g.Name}」品级 {g.ItemLevel}";

                    if (g.IsCurrent)
                        ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), label + "  ← 当前");
                    else
                        ImGui.TextUnformatted(label);

                    ImGui.SameLine();
                    if (inPoolIndexes.Contains(g.Index))
                    {
                        ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), "● 在池中");
                    }
                    else if (superseded.Contains(g.Index))
                    {
                        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "○ 同职业有更高品级套装");
                    }
                    else
                    {
                        ImGui.TextDisabled("○ 不在池中");
                    }
                }
            }
        }

        if (ImGui.CollapsingHeader("抽取历史", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (config.History.Count == 0)
            {
                ImGui.TextDisabled("还没有记录。");
            }
            else
            {
                foreach (var h in config.History)
                    ImGui.TextUnformatted(h);

                if (ImGui.Button("清空历史"))
                {
                    config.History.Clear();
                    this.plugin.SaveConfig();
                }
            }
        }
    }

    // ==================================================================
    // 抽中统计
    // ==================================================================
    private void DrawStatsTab(Configuration config)
    {
        var jobs = this.plugin.Jobs;

        var total = config.TotalDraws;
        var countedJobs = config.DrawCounts.Count(kv => kv.Value > 0);
        var enabledJobs = jobs.All.Count(j => config.IsJobEnabled(j.Id));

        ImGui.TextUnformatted($"累计抽签: {total} 次");
        ImGui.SameLine();
        ImGui.TextDisabled($"| 被抽中过的职业: {countedJobs} 个 | 当前已勾选职业: {enabledJobs} 个");

        if (total == 0)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.8f, 0.4f, 1f),
                "还没有抽签记录。抽一次之后这里就会开始统计。");
        }

        ImGui.Separator();

        // ---- 操作 ----
        if (ImGui.Button("重置统计"))
        {
            config.ResetDrawCounts();
            this.plugin.SaveConfig();
            this.SetStatus("抽中统计已重置。", false);
        }

        ImGui.SameLine();
        if (ImGui.Button("复制统计到聊天栏"))
            this.PrintStatsToChat(config);

        ImGui.SameLine();
        var onlyDrawn = this.statsOnlyDrawn;
        if (ImGui.Checkbox("只看抽中过的职业", ref onlyDrawn))
            this.statsOnlyDrawn = onlyDrawn;

        ImGui.SameLine();
        ImGui.TextDisabled("点击表头可排序");

        ImGui.Separator();

        // ---- 表格 ----
        var rows = jobs.All
            .Select(j => (Job: j, Count: config.DrawCountOf(j.Id)))
            .Where(x => !this.statsOnlyDrawn || x.Count > 0)
            .ToList();

        if (rows.Count == 0)
        {
            ImGui.TextDisabled("没有可显示的职业。");
            return;
        }

        // 排序：按次数降序，次数相同按职业 ID 升序（稳定）
        rows.Sort((a, b) =>
        {
            var byCount = b.Count.CompareTo(a.Count);
            return byCount != 0 ? byCount : a.Job.Id.CompareTo(b.Job.Id);
        });

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.SizingStretchProp;

        if (ImGui.BeginTable("##drawStats", 5, flags))
        {
            ImGui.TableSetupColumn("职业");
            ImGui.TableSetupColumn("职能");
            ImGui.TableSetupColumn("被抽中次数");
            ImGui.TableSetupColumn("占比");
            ImGui.TableSetupColumn("是否在池中");
            ImGui.TableHeadersRow();

            foreach (var (job, count) in rows)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                if (job.IsSpecial)
                    ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), $"{job.Name}(特殊)");
                else
                    ImGui.TextUnformatted(job.Name);

                ImGui.TableNextColumn();
                ImGui.TextDisabled(JobCatalog.RoleName(job.Role));

                ImGui.TableNextColumn();
                if (count > 0)
                    ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), count.ToString());
                else
                    ImGui.TextDisabled("0");

                ImGui.TableNextColumn();
                ImGui.TextDisabled(total > 0 ? $"{count * 100.0 / total:F1}%" : "—");

                ImGui.TableNextColumn();
                if (!config.IsJobEnabled(job.Id))
                    ImGui.TextDisabled("未勾选");
                else if (config.ExcludeSpecialJobs && job.IsSpecial)
                    ImGui.TextDisabled("被特殊职业开关排除");
                else if (!this.plugin.JobsMissingGearsets(job.Role).Contains(job.Name))
                    ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), "在池");
                else
                    ImGui.TextDisabled("无可用套装");
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("说明：「占比」按累计抽签次数计算；被抽中次数会写入配置文件长期保留。");
    }

    private void PrintStatsToChat(Configuration config)
    {
        var total = config.TotalDraws;
        if (total == 0)
        {
            Plugin.ChatGui.Print("[随机职业] 还没有抽签记录。");
            return;
        }

        Plugin.ChatGui.Print($"[随机职业] 抽中统计（累计 {total} 次）：");

        foreach (var (job, count) in this.plugin.Jobs.All
                     .Select(j => (Job: j, Count: config.DrawCountOf(j.Id)))
                     .Where(x => x.Count > 0)
                     .OrderByDescending(x => x.Count))
        {
            Plugin.ChatGui.Print($"  {job.Name}: {count} 次（{count * 100.0 / total:F1}%）");
        }
    }

    public void Dispose()
    {
        // 没有需要手动释放的资源
    }
}
