using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using InfoTransfer.Models;

namespace InfoTransfer.Services;

/// <summary>
/// 信号监控通用服务（期货和股票共用）
/// </summary>
public class SignalMonitorService
{
    private readonly DatabaseService _databaseService;
    private System.Timers.Timer? _monitorTimer;
    private bool _isMonitoring;
    private bool _isDisposing;
    private readonly object _logFileLock = new();

    public event EventHandler<LogEventArgs>? OnLog;
    public bool IsMonitoring => _isMonitoring;

    public SignalMonitorService(DatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    /// <summary>
    /// 启动期货监控
    /// </summary>
    public void StartFutureMonitor(GDSignalConfig config)
    {
        StartMonitoring(config, true);
    }

    /// <summary>
    /// 启动股票监控
    /// </summary>
    public void StartStockMonitor(GDStockSignalConfig config)
    {
        StartMonitoring(config, false);
    }

    /// <summary>
    /// 停止监控
    /// </summary>
    public void StopMonitor()
    {
        _isMonitoring = false;
        StopMonitorTimer();
        Log("INFO", "监控已停止");
    }

    private void StartMonitoring(object config, bool isFuture)
    {
        StopMonitorTimer();

        int intervalMinutes;
        if (isFuture && config is GDSignalConfig futureConfig)
        {
            intervalMinutes = futureConfig.MonitorIntervalMinutes;
        }
        else if (!isFuture && config is GDStockSignalConfig stockConfig)
        {
            intervalMinutes = stockConfig.MonitorIntervalMinutes;
        }
        else
        {
            intervalMinutes = 30;
        }

        var intervalMs = intervalMinutes * 60 * 1000;
        if (intervalMs < 10000) intervalMs = 10000;

        _monitorTimer = new System.Timers.Timer(intervalMs);
        _monitorTimer.Elapsed += async (s, e) =>
        {
            if (_isMonitoring && !_isDisposing)
            {
                await CheckConditionsAsync(isFuture);
            }
        };
        _monitorTimer.Start();

        _isMonitoring = true;
        Log("INFO", $"监控定时器已启动，间隔 {intervalMinutes} 分钟");

        Task.Run(async () =>
        {
            if (!_isDisposing)
            {
                await CheckConditionsAsync(isFuture);
            }
        });
    }

    private void StopMonitorTimer()
    {
        if (_monitorTimer != null)
        {
            _monitorTimer.Stop();
            _monitorTimer.Dispose();
            _monitorTimer = null;
        }
    }

    /// <summary>
    /// 执行一次检查（外部调用）
    /// </summary>
    public async Task CheckOnceAsync(bool isFuture)
    {
        if (!_isMonitoring)
        {
            _isMonitoring = true;
        }
        await CheckConditionsAsync(isFuture);
    }

    private async Task CheckConditionsAsync(bool isFuture)
    {
        try
        {
            Log("INFO", $"[{DateTime.Now:HH:mm:ss}] 开始检查信号...");

            object? config = null;
            if (isFuture)
            {
                var configs = _databaseService.GetAllGDSignalConfigs();
                if (configs.Count > 0) config = configs[0];
            }
            else
            {
                var configs = _databaseService.GetAllGDStockSignalConfigs();
                if (configs.Count > 0) config = configs[0];
            }

            if (config == null)
            {
                Log("WARN", "未找到监控配置");
                return;
            }

            if (!IsInMonitorPeriod(config))
            {
                Log("INFO", $"[{DateTime.Now:HH:mm:ss}] 不在监控时间段内");
                return;
            }

            var data = await FetchSignalDataAsync(config, isFuture);
            if (data == null)
            {
                Log("ERROR", "获取信号数据失败");
                return;
            }

            var triggerResults = CheckConditions(data, config);
            int totalProducts = triggerResults.Sum(r => r.Products.Count);

            if (totalProducts > 0)
            {
                Log("INFO", $"检测到 {triggerResults.Count} 个策略共 {totalProducts} 个品种满足条件");
                await PushNotificationAsync(triggerResults, config, isFuture);
            }
            else
            {
                Log("INFO", $"[{DateTime.Now:HH:mm:ss}] 运行中...");
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"检查监控条件失败: {ex.Message}");
        }
    }

    private bool IsInMonitorPeriod(object config)
    {
        var now = DateTime.Now;
        var dayOfWeek = now.DayOfWeek;

        if (dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday)
            return false;

        string startTime, endTime;
        bool monitorNight, useFixed, monitorEnabled;
        string fixedMinutes;
        int intervalMinutes;
        string nightStart, nightEnd;

        if (config is GDSignalConfig futureConfig)
        {
            startTime = futureConfig.MonitorStartTime;
            endTime = futureConfig.MonitorEndTime;
            monitorNight = futureConfig.MonitorNightSession;
            useFixed = futureConfig.UseFixedTimePoints;
            fixedMinutes = futureConfig.FixedTimeMinutes;
            intervalMinutes = futureConfig.MonitorIntervalMinutes;
            nightStart = futureConfig.NightSessionStartTime;
            nightEnd = futureConfig.NightSessionEndTime;
            monitorEnabled = true;
        }
        else if (config is GDStockSignalConfig stockConfig)
        {
            startTime = stockConfig.MonitorStartTime;
            endTime = stockConfig.MonitorEndTime;
            monitorNight = stockConfig.MonitorNightSession;
            useFixed = stockConfig.UseFixedTimePoints;
            fixedMinutes = stockConfig.FixedTimeMinutes;
            intervalMinutes = stockConfig.MonitorIntervalMinutes;
            nightStart = stockConfig.NightSessionStartTime;
            nightEnd = stockConfig.NightSessionEndTime;
            monitorEnabled = true;
        }
        else
        {
            return false;
        }

        if (!monitorEnabled)
            return false;

        if (useFixed)
        {
            var minute = now.Minute;
            var fixedMins = fixedMinutes
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var m) ? m : -1)
                .Where(m => m >= 0 && m < 60)
                .ToList();

            if (!fixedMins.Contains(minute))
                return false;
        }
        else
        {
            var minute = now.Minute;
            if (minute % intervalMinutes != 0)
                return false;
        }

        if (TimeSpan.TryParse(startTime, out var start) &&
            TimeSpan.TryParse(endTime, out var end))
        {
            var currentTime = now.TimeOfDay;
            if (currentTime >= start && currentTime <= end)
                return true;
        }

        if (monitorNight)
        {
            if (TimeSpan.TryParse(nightStart, out var ns) &&
                TimeSpan.TryParse(nightEnd, out var ne))
            {
                var currentTime = now.TimeOfDay;
                if (ns > ne)
                {
                    if (currentTime >= ns || currentTime <= ne)
                        return true;
                }
                else
                {
                    if (currentTime >= ns && currentTime <= ne)
                        return true;
                }
            }
        }

        return false;
    }

    private async Task<JToken?> FetchSignalDataAsync(object config, bool isFuture)
    {
        string apiBaseUrl, apiToken;

        if (config is GDSignalConfig futureConfig)
        {
            apiBaseUrl = futureConfig.ApiBaseUrl;
            apiToken = futureConfig.ApiToken;
        }
        else if (config is GDStockSignalConfig stockConfig)
        {
            apiBaseUrl = stockConfig.ApiBaseUrl;
            apiToken = stockConfig.ApiToken;
        }
        else
        {
            return null;
        }

        try
        {
            var apiUrl = $"{apiBaseUrl}/ai-api/solutions/SignalMonitor/same";
            var allStrategys = new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" };
            var paramList = allStrategys.Select(s => $"Strategys={Uri.EscapeDataString(s)}").ToList();
            var fullUrl = apiUrl + "?" + string.Join("&", paramList);

            Log("INFO", $"调用API: {fullUrl}");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrWhiteSpace(apiToken))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);
            }

            var response = await client.GetAsync(fullUrl);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                Log("INFO", $"API返回数据长度: {content.Length} 字节");
                return JToken.Parse(content);
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Log("ERROR", $"API返回错误: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"获取信号数据异常: {ex.Message}");
        }

        return null;
    }

    private List<StrategyTriggerResult> CheckConditions(JToken data, object config)
    {
        var results = new List<StrategyTriggerResult>();

        var enabledStrategies = GetEnabledStrategies(config);
        if (enabledStrategies.Count == 0)
        {
            Log("WARN", "没有勾选任何主要监控策略");
            return results;
        }

        var dataArray = data["data"] as JArray;
        if (dataArray == null) return results;

        bool checkRealTimeStop, checkRemainingRisk;
        double realTimeStopThreshold, remainingRiskThreshold;

        if (config is GDSignalConfig futureConfig)
        {
            checkRealTimeStop = futureConfig.EnableRealTimeStopPriceDiffRateCondition;
            checkRemainingRisk = futureConfig.EnableRemainingRiskCondition;
            realTimeStopThreshold = futureConfig.RealTimeStopPriceDiffRateValue / 100.0;
            remainingRiskThreshold = futureConfig.RemainingRiskValue / 100.0;
        }
        else if (config is GDStockSignalConfig stockConfig)
        {
            checkRealTimeStop = stockConfig.EnableRealTimeStopPriceDiffRateCondition;
            checkRemainingRisk = stockConfig.EnableRemainingRiskCondition;
            realTimeStopThreshold = stockConfig.RealTimeStopPriceDiffRateValue / 100.0;
            remainingRiskThreshold = stockConfig.RemainingRiskValue / 100.0;
        }
        else
        {
            return results;
        }

        Log("CHECK", $"勾选的策略: {string.Join(", ", enabledStrategies)}");
        Log("CHECK", $"realTimeStopPriceDiffRate > {realTimeStopThreshold * 100:F2}% (启用: {checkRealTimeStop})");
        Log("CHECK", $"remainingRisk < {remainingRiskThreshold * 100:F2}% (启用: {checkRemainingRisk})");

        foreach (var currentStrategy in enabledStrategies)
        {
            var higherStrategies = GetHigherStrategies(currentStrategy);
            var triggeredProducts = new List<ProductTriggerInfo>();

            Log("CHECK", $"========== 判定策略: {currentStrategy} ==========");
            Log("CHECK", $"向上判定策略: {(higherStrategies.Count > 0 ? string.Join(", ", higherStrategies) : "无")}");

            var candidateProducts = new List<(string productId, string direction, double realTimeStop, double remainingRisk, JObject items)>();

            foreach (var productData in dataArray)
            {
                var productId = productData["productId"]?.ToString();
                if (string.IsNullOrEmpty(productId)) continue;

                var items = productData["items"] as JObject;
                if (items == null) continue;

                var currentStrategyData = items[currentStrategy] as JObject;
                if (currentStrategyData == null) continue;

                var direction = currentStrategyData["direction"]?.ToString() ?? "";
                if (direction.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;

                var realTimeStopPriceDiffRate = currentStrategyData["realTimeStopPriceDiffRate"]?.Value<double>() ?? 0;

                if (checkRealTimeStop && realTimeStopPriceDiffRate <= realTimeStopThreshold) continue;

                var remainingRisk = currentStrategyData["remainingRisk"]?.Value<double>() ?? 0;
                candidateProducts.Add((productId, direction, realTimeStopPriceDiffRate, remainingRisk, items));

                Log("CHECK", $"  {productId}: direction={direction}, realTimeStop={realTimeStopPriceDiffRate * 100:F2}%");
            }

            Log("CHECK", $"符合条件的品种数: {candidateProducts.Count}");

            if (candidateProducts.Count == 0) continue;

            foreach (var candidate in candidateProducts)
            {
                bool allMatch = true;
                var higherDirections = new Dictionary<string, string>();

                foreach (var higherStrategy in higherStrategies)
                {
                    var higherStrategyData = candidate.items[higherStrategy] as JObject;
                    if (higherStrategyData == null)
                    {
                        allMatch = false;
                        break;
                    }

                    var higherDir = higherStrategyData["direction"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(higherDir) || higherDir.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        allMatch = false;
                        break;
                    }

                    bool dirMatch = higherDir.Equals(candidate.direction, StringComparison.OrdinalIgnoreCase);
                    if (!dirMatch)
                    {
                        allMatch = false;
                        break;
                    }

                    higherDirections[higherStrategy] = higherDir;
                }

                if (allMatch)
                {
                    var lastHigherStrategy = higherStrategies.LastOrDefault();
                    double highestRisk = 0;
                    if (!string.IsNullOrEmpty(lastHigherStrategy) && candidate.items[lastHigherStrategy] is JObject lastHigherData)
                    {
                        highestRisk = lastHigherData["remainingRisk"]?.Value<double>() ?? 0;
                    }

                    triggeredProducts.Add(new ProductTriggerInfo
                    {
                        ProductId = candidate.productId,
                        CurrentDirection = candidate.direction,
                        HigherDirections = higherDirections,
                        RealTimeStopPriceDiffRate = candidate.realTimeStop,
                        RemainingRisk = highestRisk > 0 ? highestRisk : candidate.remainingRisk
                    });
                }
            }

            if (checkRemainingRisk && triggeredProducts.Count > 0)
            {
                var filteredProducts = triggeredProducts.Where(p => p.RemainingRisk < remainingRiskThreshold).ToList();
                Log("CHECK", $"剩余风险检查后: {filteredProducts.Count}/{triggeredProducts.Count}");
                triggeredProducts = filteredProducts;
            }

            if (triggeredProducts.Count > 0)
            {
                results.Add(new StrategyTriggerResult
                {
                    StrategyName = currentStrategy,
                    Products = triggeredProducts
                });
            }
        }

        Log("CHECK", $"满足条件的策略数: {results.Count}, 总品种数: {results.Sum(r => r.Products.Count)}");
        return results;
    }

    private List<string> GetEnabledStrategies(object config)
    {
        var strategies = new List<string>();

        if (config is GDSignalConfig futureConfig)
        {
            if (futureConfig.EnableGD15) strategies.Add("GD15");
            if (futureConfig.EnableGD20) strategies.Add("GD20");
            if (futureConfig.EnableGD25) strategies.Add("GD25");
            if (futureConfig.EnableGD30) strategies.Add("GD30");
            if (futureConfig.EnableGD35) strategies.Add("GD35");
            if (futureConfig.EnableGD40) strategies.Add("GD40");
        }
        else if (config is GDStockSignalConfig stockConfig)
        {
            if (stockConfig.EnableGD15) strategies.Add("GD15");
            if (stockConfig.EnableGD20) strategies.Add("GD20");
            if (stockConfig.EnableGD25) strategies.Add("GD25");
            if (stockConfig.EnableGD30) strategies.Add("GD30");
            if (stockConfig.EnableGD35) strategies.Add("GD35");
            if (stockConfig.EnableGD40) strategies.Add("GD40");
        }

        return strategies;
    }

    private List<string> GetHigherStrategies(string mainStrategy)
    {
        var allStrategies = new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" };
        var index = Array.IndexOf(allStrategies, mainStrategy);
        if (index < 0 || index >= allStrategies.Length - 1)
            return new List<string>();

        return allStrategies.Skip(index + 1).ToList();
    }

    private async Task PushNotificationAsync(List<StrategyTriggerResult> results, object config, bool isFuture)
    {
        string terminalId;
        bool enableText;

        if (config is GDSignalConfig futureConfig)
        {
            terminalId = futureConfig.TerminalId;
            enableText = futureConfig.EnableText;
        }
        else if (config is GDStockSignalConfig stockConfig)
        {
            terminalId = stockConfig.TerminalId;
            enableText = stockConfig.EnableText;
        }
        else
        {
            return;
        }

        try
        {
            if (enableText)
            {
                await PushTextMessageAsync(results, terminalId, isFuture);
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"推送通知失败: {ex.Message}");
        }
    }

    private async Task PushTextMessageAsync(List<StrategyTriggerResult> results, string terminalId, bool isFuture)
    {
        var messageContent = BuildMessageContent(results, isFuture);
        Log("INFO", $"准备推送文本消息，长度: {messageContent.Length}");

        var messageType = isFuture ? "GD_Signal" : "GD_Stock_Signal";

        var feishuMessage = new FeishuMessage
        {
            MessageType = messageType,
            TerminalId = terminalId,
            Method = "text",
            Status = "Pending",
            Body = messageContent,
            ReceivedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            SourceId = null
        };

        var messageId = _databaseService.CreateMessage(feishuMessage);
        Log("INFO", $"消息已插入数据库，ID: {messageId}");

        await Task.CompletedTask;
    }

    private string BuildMessageContent(List<StrategyTriggerResult> results, bool isFuture)
    {
        var messageType = isFuture ? "期货" : "股票";
        var messageBuilder = new StringBuilder();

        messageBuilder.AppendLine($"【GD{messageType}信号监控】触发提醒");
        messageBuilder.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm}");

        foreach (var result in results)
        {
            messageBuilder.AppendLine();
            messageBuilder.AppendLine($"【{result.StrategyName}】触发提醒条件");

            messageBuilder.AppendLine("品种列表:");
            foreach (var product in result.Products.OrderBy(p => p.ProductId))
            {
                messageBuilder.AppendLine($"  {product.ProductId}");
                messageBuilder.AppendLine($"    实时止损价差: {product.RealTimeStopPriceDiffRate:P2}");
                messageBuilder.AppendLine($"    剩余风险: {product.RemainingRisk:P2}");
                messageBuilder.AppendLine($"    方向: {product.CurrentDirection}");
            }
        }

        return messageBuilder.ToString();
    }

    private void Log(string level, string message)
    {
        var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(entry);
        OnLog?.Invoke(this, new LogEventArgs { Level = level, Message = message });
    }

    public void Dispose()
    {
        _isDisposing = true;
        StopMonitorTimer();
    }
}

public class LogEventArgs : EventArgs
{
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>
/// 按策略分组的结果（与 GDSignalConfigWindow 保持兼容）
/// </summary>
public class StrategyTriggerResult
{
    public string StrategyName { get; set; } = "";
    public List<ProductTriggerInfo> Products { get; set; } = new();
}

public class ProductTriggerInfo
{
    public string ProductId { get; set; } = "";
    public string CurrentDirection { get; set; } = "";
    public Dictionary<string, string> HigherDirections { get; set; } = new();
    public double RealTimeStopPriceDiffRate { get; set; }
    public double RemainingRisk { get; set; }
}
