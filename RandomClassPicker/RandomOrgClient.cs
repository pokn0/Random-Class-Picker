using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;

namespace RandomClassPicker;

/// <summary>一次抽签的结果。</summary>
public readonly record struct RandomDraw(int Value, bool IsTrueRandom, string Detail);

/// <summary>
/// random.org 真随机客户端。
///
/// 说明：random.org 的随机数来自大气噪声（ANU 量子真空涨落），
/// 属于物理熵源，不是伪随机算法，所以符合"真随机"的要求。
///
/// 用的是 <c>/integers/</c> 接口：
///   https://www.random.org/integers/?num=1&amp;min=1&amp;max=100&amp;col=1&amp;base=10&amp;format=plain&amp;rnd=new
/// 该接口有配额限制（免费额度约每天 1000 次请求、每分钟并发受限），
/// 超限时会返回 HTTP 503 或 200 + 一段以 "Error:" 开头的文本，两种情况都要兜住。
/// </summary>
public sealed partial class RandomOrgClient : IDisposable
{
    private const string Endpoint = "https://www.random.org/integers/";

    private readonly HttpClient http;
    private readonly IPluginLog log;

    public RandomOrgClient(IPluginLog log)
    {
        this.log = log;
        this.http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        // random.org 要求带上能识别来源的 User-Agent
        this.http.DefaultRequestHeaders.UserAgent.ParseAdd("RandomClassPicker/0.0.0.1 (FFXIV Dalamud plugin)");
        this.http.DefaultRequestHeaders.Accept.ParseAdd("text/plain");
    }

    /// <summary>最近一次用到的来源说明，给界面显示用。</summary>
    public string LastSourceDetail { get; private set; } = "尚未抽签";

    /// <summary>
    /// 抽一个 [min, max] 区间内的整数（闭区间）。
    /// 按 <paramref name="source"/> 决定是否允许降级到本地随机。
    /// 这个方法会阻塞（网络 IO），必须在后台线程调用。
    /// </summary>
    public RandomDraw Draw(int min, int max, RandomSource source)
    {
        if (min >= max)
            throw new ArgumentException($"区间非法: [{min}, {max}]");

        if (source != RandomSource.LocalFallback)
        {
            var online = this.TryDrawFromRandomOrg(min, max);
            if (online.HasValue)
            {
                this.LastSourceDetail = "random.org（大气噪声真随机）";
                return new RandomDraw(online.Value, true, this.LastSourceDetail);
            }

            if (source == RandomSource.RandomOrgOnly)
            {
                this.LastSourceDetail = "random.org 不可用，且已设置为「只用真随机」";
                throw new InvalidOperationException(this.LastSourceDetail);
            }
        }

        var local = Random.Shared.Next(min, max + 1);
        this.LastSourceDetail = "本地伪随机（random.org 不可用）";
        return new RandomDraw(local, false, this.LastSourceDetail);
    }

    /// <summary>给界面上的"测试连接"按钮用。</summary>
    public string TestConnection()
    {
        var value = this.TryDrawFromRandomOrg(1, 100);
        var msg = value.HasValue
            ? $"连接正常，random.org 返回了 {value.Value}（真随机可用）"
            : "连接失败：拿不到 random.org 的随机数，请检查网络/代理或配额";
        this.LastSourceDetail = msg;
        return msg;
    }

    private int? TryDrawFromRandomOrg(int min, int max)
    {
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{Endpoint}?num=1&min={min}&max={max}&col=1&base=10&format=plain&rnd=new");

        try
        {
            using var response = this.http.GetAsync(url).GetAwaiter().GetResult();

            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
            {
                this.log.Warning($"[RandomClassPicker] random.org 限流/配额用尽: HTTP {(int)response.StatusCode}");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                this.log.Warning($"[RandomClassPicker] random.org 返回 HTTP {(int)response.StatusCode}");
                return null;
            }

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            // 200 也可能是错误文本，必须校验解析结果
            var parsed = ParseFirstInteger(body, min, max);
            if (parsed.HasValue)
                return parsed;

            this.log.Warning($"[RandomClassPicker] random.org 响应无法解析: {Truncate(body)}");
            return null;
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[RandomClassPicker] 请求 random.org 失败");
            return null;
        }
    }

    /// <summary>
    /// 从响应里取第一个落在 [min, max] 内的整数。
    /// 用"只认区间内的数字"来过滤，这样 HTML 错误页或 "Error: ..." 文本都不会被误当成随机数。
    /// </summary>
    private static int? ParseFirstInteger(string body, int min, int max)
    {
        foreach (Match match in IntegerRegex().Matches(body))
        {
            if (int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && value >= min && value <= max)
            {
                return value;
            }
        }

        return null;
    }

    private static string Truncate(string text)
        => text.Length <= 200 ? text : text[..200] + "...";

    [GeneratedRegex(@"\d+")]
    private static partial Regex IntegerRegex();

    public void Dispose() => this.http.Dispose();
}
