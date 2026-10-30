using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Beautiful.Core.Dock;

namespace Beautiful.App.Services;

/// <summary>
/// Dock 小组件的天气数据（Phase 7-T3）：<c>wttr.in</c> 免密钥、按出口 IP 自动定位，返回 WWO 天气代码。
/// <para>
/// 【为什么不用 Open-Meteo】它需要经纬度，而取经纬度要么接 Windows 定位（还要用户授权）、
/// 要么再拉一个 IP 定位服务——两步都会失败。wttr.in 的 <c>?format=j1</c> 一步给出
/// 温度 + 天气代码 + 城市名，代价是可用性不如商业 API，因此这里**完全容错**：
/// 失败就保留上一次的数据（或显示 <c>--°</c>），绝不因为天气挂了影响 Dock 本身。
/// </para>
/// <para>
/// 【缓存】15 分钟内不重复请求；界面只在数据变化时收到一次 <see cref="Updated"/>。
/// 请求全部在后台线程，UI 线程只做一次属性替换。
/// </para>
/// </summary>
public sealed class WeatherService : IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private DateTimeOffset _fetchedAt;
    private bool _requesting;
    private bool _failureLogged;

    /// <summary>最近一次成功的读数；从未成功时为 <c>null</c>。</summary>
    public WeatherReading? Current { get; private set; }

    /// <summary>读数或失败状态变化（UI 据此刷新小组件第二行）。</summary>
    public event EventHandler? Updated;

    public WeatherService()
    {
        _http = new HttpClient { Timeout = RequestTimeout };
        // wttr.in 对浏览器 UA 会返回 HTML 引导页，必须用 curl 风格 UA 才给 JSON。
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8.4.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>取一次天气（带缓存与去重）。可以随时调用，内部自行判断是否需要真的发请求。</summary>
    public void RefreshIfStale()
    {
        if (_requesting || !DockWidget.IsStale(_fetchedAt, DateTimeOffset.Now, CacheTtl))
        {
            return;
        }

        _requesting = true;
        _ = FetchAsync(_cts.Token);
    }

    private async Task FetchAsync(CancellationToken token)
    {
        try
        {
            var json = await _http.GetStringAsync("https://wttr.in/?format=j1", token)
                .ConfigureAwait(false);
            var reading = Parse(json);
            if (reading is null)
            {
                LogFailureOnce("返回内容无法解析");
                return;
            }

            Current = reading;
            _fetchedAt = DateTimeOffset.Now;
            _failureLogged = false;
            Logger.Info($"Dock 天气：{reading.City} {reading.TemperatureC:0.#}° 代码 {reading.Code}");
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // 退出或超时：不是错误，不写日志刷屏
        }
        catch (Exception ex)
        {
            LogFailureOnce($"{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            _requesting = false;
        }
    }

    /// <summary>解析 wttr.in 的 j1 响应。字段缺失时返回 null（宁可不显示，也不显示 0°）。</summary>
    internal static WeatherReading? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("current_condition", out var conditions)
                || conditions.ValueKind != JsonValueKind.Array
                || conditions.GetArrayLength() == 0)
            {
                return null;
            }

            var current = conditions[0];
            if (!current.TryGetProperty("temp_C", out var tempNode)
                || !double.TryParse(tempNode.GetString(), out var temperature))
            {
                return null;
            }

            var code = current.TryGetProperty("weatherCode", out var codeNode)
                && int.TryParse(codeNode.GetString(), out var parsedCode)
                    ? parsedCode
                    : -1;

            // 英文描述：代码不在映射表里时用它兜底（wttr.in 实测会给标准外的代码）
            var description = string.Empty;
            if (current.TryGetProperty("weatherDesc", out var descriptions)
                && descriptions.ValueKind == JsonValueKind.Array
                && descriptions.GetArrayLength() > 0
                && descriptions[0].TryGetProperty("value", out var descriptionNode))
            {
                description = descriptionNode.GetString() ?? string.Empty;
            }

            var city = string.Empty;
            if (root.TryGetProperty("nearest_area", out var areas)
                && areas.ValueKind == JsonValueKind.Array
                && areas.GetArrayLength() > 0
                && areas[0].TryGetProperty("areaName", out var names)
                && names.ValueKind == JsonValueKind.Array
                && names.GetArrayLength() > 0
                && names[0].TryGetProperty("value", out var nameNode))
            {
                city = nameNode.GetString() ?? string.Empty;
            }

            return new WeatherReading(temperature, code, city, description);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void LogFailureOnce(string reason)
    {
        if (_failureLogged)
        {
            return;
        }

        _failureLogged = true;
        Logger.Warn($"Dock 天气：本次获取失败（{reason}），继续显示上一次读数");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _http.Dispose();
    }
}

/// <summary>一次天气读数（温度摄氏 + WWO 代码 + 城市名）。</summary>
public sealed record WeatherReading(double TemperatureC, int Code, string City, string Description)
{
    /// <summary>Dock 第二行文字，例如 <c>23° 晴</c>（代码未知时用英文描述兜底）。</summary>
    public string Detail => DockWidget.Detail(TemperatureC, Code, Description);
}
