using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using InfoTransfer.Models;
using InfoTransfer.Services;
using Newtonsoft.Json.Linq;

namespace InfoTransfer;

public partial class GDStopLossMonitorWindow : Window
{
    private readonly DatabaseService _databaseService;
    private readonly ConfigService _configService;
    private readonly Action<string, string> _logCallback;
    private readonly object _logLock = new();
    private System.Threading.Timer? _monitorTimer;
    private bool _isMonitoring;
    private DateTime? _lastPushTime;
    private JToken? _lastPushData;
    private List<string> _selectedTerminalIds = new();
    private readonly string _logFilePath;
    private List<CheckBox> _terminalCheckBoxes = new();
    private int _scanIntervalMinutes = 1;
    private bool _sendText = true;
    private bool _sendImage = false;
    // 闪烁缓存: key="策略:品种ID", value=上次变化时间（用于5分钟内防闪烁）
    private readonly Dictionary<string, DateTime> _flickerCache = new();
    private readonly object _flickerLock = new();
    private const int FlickerCacheMinutes = 5;

    public GDStopLossMonitorWindow(DatabaseService databaseService, ConfigService configService, Action<string, string> logCallback)
    {
        InitializeComponent();

        _databaseService = databaseService;
        _configService = configService;
        _logCallback = logCallback;

        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        _logFilePath = Path.Combine(appDir, $"stop_loss_monitor_{DateTime.Now:yyyyMMdd}.log");

        // 绑定事件
        ChkSendText.Checked += ChkSendOption_Changed;
        ChkSendText.Unchecked += ChkSendOption_Changed;
        ChkSendImage.Checked += ChkSendOption_Changed;
        ChkSendImage.Unchecked += ChkSendOption_Changed;

        LoadTerminals();
        LoadSendOptions();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_monitorTimer != null)
        {
            _monitorTimer?.Dispose();
            _monitorTimer = null;
        }
        _isMonitoring = false;
    }

    private void LoadSendOptions()
    {
        try
        {
            var configs = _databaseService.GetAllGDSignalConfigs();
            if (configs.Count > 0)
            {
                _sendText = configs[0].EnableText;
                _sendImage = configs[0].EnableImage;
                Dispatcher.Invoke(() =>
                {
                    ChkSendText.IsChecked = _sendText;
                    ChkSendImage.IsChecked = _sendImage;
                });
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"加载发送选项失败: {ex.Message}");
        }
    }

    private void SaveSendOptions()
    {
        try
        {
            var configs = _databaseService.GetAllGDSignalConfigs();
            if (configs == null || configs.Count == 0)
            {
                return;
            }
            var config = configs[0];
            config.EnableText = ChkSendText.IsChecked == true;
            config.EnableImage = ChkSendImage.IsChecked == true;
            _databaseService.SaveGDSignalConfig(config);
        }
        catch (Exception ex)
        {
            Log("ERROR", $"保存发送选项失败: {ex.Message}");
        }
    }

    private void LoadTerminals()
    {
        var terminals = _databaseService.GetAllTerminalConfigs();
        TerminalCheckBoxes.Items.Clear();
        _terminalCheckBoxes.Clear();

        var savedTerminals = GetSavedTerminalIds();

        foreach (var terminal in terminals)
        {
            var checkBox = new CheckBox
            {
                Content = terminal.TerminalId,
                Tag = terminal.TerminalId,
                Margin = new Thickness(0, 0, 15, 5),
                IsChecked = savedTerminals.Contains(terminal.TerminalId)
            };
            checkBox.Checked += TerminalCheckBox_Changed;
            checkBox.Unchecked += TerminalCheckBox_Changed;
            TerminalCheckBoxes.Items.Add(checkBox);
            _terminalCheckBoxes.Add(checkBox);
        }
    }

    private List<string> GetSavedTerminalIds()
    {
        var ids = new List<string>();
        try
        {
            var configs = _databaseService.GetAllGDSignalConfigs();
            if (configs.Count > 0 && !string.IsNullOrEmpty(configs[0].TerminalId))
            {
                ids = configs[0].TerminalId.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
        }
        catch { }
        return ids;
    }

    /// <summary>
    /// 获取已勾选的策略列表
    /// </summary>
    private List<string> GetEnabledStrategies()
    {
        var strategies = new List<string>();
        try
        {
            var configs = _databaseService.GetAllGDSignalConfigs();
            if (configs.Count > 0)
            {
                var config = configs[0];
                if (config.EnableGD15) strategies.Add("GD15");
                if (config.EnableGD20) strategies.Add("GD20");
                if (config.EnableGD25) strategies.Add("GD25");
                if (config.EnableGD30) strategies.Add("GD30");
                if (config.EnableGD35) strategies.Add("GD35");
                if (config.EnableGD40) strategies.Add("GD40");
            }
        }
        catch { }
        return strategies;
    }

    private void ChkSendOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_databaseService == null) return;
        SaveSendOptions();
    }

    private void TerminalCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _selectedTerminalIds = _terminalCheckBoxes
            .Where(cb => cb.IsChecked == true)
            .Select(cb => cb.Tag?.ToString())
            .Where(t => !string.IsNullOrEmpty(t))
            .Cast<string>()
            .ToList();
        SaveTerminalSelection();
    }

    private void SaveTerminalSelection()
    {
        try
        {
            var configs = _databaseService.GetAllGDSignalConfigs();
            if (configs.Count > 0)
            {
                var config = configs[0];
                config.TerminalId = string.Join(",", _selectedTerminalIds);
                _databaseService.SaveGDSignalConfig(config);
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"保存终端选择失败: {ex.Message}");
        }
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        // 获取扫描间隔
        if (!int.TryParse(TxtScanInterval.Text, out _scanIntervalMinutes) || _scanIntervalMinutes < 1 || _scanIntervalMinutes > 60)
        {
            MessageBox.Show("扫描间隔必须在1-60之间", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 获取发送形式
        _sendText = ChkSendText.IsChecked == true;
        _sendImage = ChkSendImage.IsChecked == true;

        if (!_sendText && !_sendImage)
        {
            MessageBox.Show("请至少选择一种发送形式（文字或图片）", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 从勾选项获取选中的终端
        _selectedTerminalIds = _terminalCheckBoxes
            .Where(cb => cb.IsChecked == true)
            .Select(cb => cb.Tag?.ToString())
            .Where(t => !string.IsNullOrEmpty(t))
            .Cast<string>()
            .ToList();

        if (_selectedTerminalIds.Count == 0)
        {
            MessageBox.Show("请至少选择一个推送终端", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isMonitoring = true;
        BtnStart.IsEnabled = false;
        BtnStop.IsEnabled = true;
        TxtStatus.Text = "状态: 监测中";

        var sendTypes = new List<string>();
        if (_sendText) sendTypes.Add("文字");
        if (_sendImage) sendTypes.Add("图片");
        Log("INFO", $"========== 盘中止损监测启动 (间隔:{_scanIntervalMinutes}分钟, 推送至:{string.Join(",", _selectedTerminalIds)}, 形式:{string.Join("/", sendTypes)}) ==========");

        // 立即执行一次
        _ = ExecuteMonitorAsync();

        // 使用配置的间隔执行
        _monitorTimer = new System.Threading.Timer(
            async _ => await ExecuteMonitorAsync(),
            null,
            TimeSpan.FromMinutes(_scanIntervalMinutes),
            TimeSpan.FromMinutes(_scanIntervalMinutes));
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _isMonitoring = false;
        _monitorTimer?.Dispose();
        _monitorTimer = null;

        BtnStart.IsEnabled = true;
        BtnStop.IsEnabled = false;
        TxtStatus.Text = "状态: 已停止";
        TxtNextRun.Text = "下次执行: --";

        Log("INFO", "========== 盘中止损监测已停止 ==========");
    }

    private async Task ExecuteMonitorAsync()
    {
        if (!_isMonitoring) return;

        try
        {
            // 每次执行时重新读取复选框状态（支持实时修改）
            var sendText = ChkSendText.IsChecked == true;
            var sendImage = ChkSendImage.IsChecked == true;

            Dispatcher.Invoke(() =>
            {
                TxtNextRun.Text = $"下次执行: {DateTime.Now.AddMinutes(_scanIntervalMinutes):HH:mm:ss}";
            });

            Log("INFO", $"[{DateTime.Now:HH:mm:ss}] 开始执行监测...");

            // 检查是否在监控时间段内
            if (!IsInMonitorPeriod())
            {
                Log("INFO", "当前不在监控时间段内，跳过");
                return;
            }

            // 获取API数据
            var data = await FetchSignalDataAsync();
            if (data == null)
            {
                Log("ERROR", "获取信号数据失败");
                return;
            }

            // 输出每个策略的实时止损差率到日志
            LogSignalData(data);

            var dataArray = data["data"] as JArray;
            if (dataArray == null)
            {
                Log("INFO", "数据格式错误");
                return;
            }

            // 获取本次符合条件的品种列表（使用已勾选的策略）
            var enabledStrategies = GetEnabledStrategies();
            var currentFiltered = GetFilteredStrategyProducts(dataArray, enabledStrategies);
            var isFirstPush = _lastPushData == null;

            // 对比上次推送的品种列表
            var hasChanged = false;
            var changeDescription = "";
            var changedProducts = new List<(string strategy, string productId, bool isAdded)>();
            if (isFirstPush)
            {
                hasChanged = true;
                Log("INFO", "首次推送");
            }
            else
            {
                var lastArray = _lastPushData["data"] as JArray;
                if (lastArray != null)
                {
                    var previousFiltered = GetFilteredStrategyProducts(lastArray, enabledStrategies);
                    changeDescription = BuildChangeDescriptionFromFiltered(currentFiltered, previousFiltered, out changedProducts);
                    hasChanged = !string.IsNullOrEmpty(changeDescription) && changeDescription != "无变化";

                    // 防闪烁检查：如果有变化的品种在5分钟缓存中，且当前状态与缓存时相同，则忽略
                    if (hasChanged && changedProducts.Count > 0)
                    {
                        var filteredChanges = FilterFlickerChanges(changedProducts, currentFiltered, previousFiltered);
                        if (filteredChanges.Count == 0)
                        {
                            Log("INFO", "所有变化均被防闪烁机制过滤，不推送");
                            Dispatcher.Invoke(() =>
                            {
                                TxtLastResult.Text = $"上次结果: {DateTime.Now:HH:mm:ss} - 闪烁过滤";
                            });
                            return;
                        }
                        // 更新变化描述，只包含未被过滤的变化
                        changeDescription = BuildFilteredChangeText(filteredChanges, currentFiltered, previousFiltered);
                    }
                }
                else
                {
                    hasChanged = true;
                }
            }

            if (!hasChanged)
            {
                Log("INFO", "品种列表无变化，不推送任何消息");
                Dispatcher.Invoke(() =>
                {
                    TxtLastResult.Text = $"上次结果: {DateTime.Now:HH:mm:ss} - 无变化";
                });
                return;
            }

            // 品种列表有变化，发送消息
            var pushSuccess = false;

            if (sendImage)
            {
                // 生成并发送图片
                var imagePath = await GenerateSignalImageAsync(data);
                if (imagePath != null)
                {
                    var imageSuccess = await PushImageToFeishuAsync(imagePath);
                    if (imageSuccess)
                    {
                        pushSuccess = true;
                        Log("INFO", "图片发送成功");

                        // 非首次推送且有变化内容时，发送文字消息
                        if (sendText && !isFirstPush && !string.IsNullOrEmpty(changeDescription))
                        {
                            var textContent = BuildPushContent(data, changeDescription);
                            if (textContent != null)
                            {
                                await PushTextToFeishuAsync(textContent);
                                Log("INFO", "文字变化信息发送成功");
                            }
                        }

                        // 更新参考数据
                        _lastPushData = data.DeepClone();
                    }
                    // 删除临时图片
                    try { File.Delete(imagePath); } catch { }
                }
            }
            else if (sendText && !isFirstPush)
            {
                // 只发送文字（不发送图片）
                var textContent = BuildPushContent(data, changeDescription);
                if (textContent != null)
                {
                    pushSuccess = await PushTextToFeishuAsync(textContent);
                    if (pushSuccess)
                    {
                        Log("INFO", "文字消息发送成功");
                        _lastPushData = data.DeepClone();
                    }
                }
            }

            if (pushSuccess)
            {
                _lastPushTime = DateTime.Now;
                Dispatcher.Invoke(() =>
                {
                    TxtLastResult.Text = $"上次结果: {DateTime.Now:HH:mm:ss} - 推送成功";
                });
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"执行监测异常: {ex.Message}");
        }
    }

    private void LogSignalData(JToken data)
    {
        try
        {
            var dataArray = data["data"] as JArray;
            if (dataArray == null) return;

            // 只记录满足条件的品种: direction != "None" && rate >= 0 && remainingRisk >= 0
            var signalProducts = new List<(string productId, string strategy, double rate, string direction)>();

            foreach (var productData in dataArray)
            {
                var productId = productData["productId"]?.ToString() ?? "";
                var items = productData["items"] as JObject;
                if (items == null) continue;

                foreach (var strategy in new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" })
                {
                    var strategyData = items[strategy] as JObject;
                    if (strategyData != null)
                    {
                        var rate = strategyData["totalRealTimeStopPriceDiffRate"]?.Value<double>() ?? 0;
                        var direction = strategyData["direction"]?.ToString() ?? "";
                        var remainingRisk = strategyData["remainingRisk"]?.Value<double>() ?? 0;
                        // 筛选条件: direction不能为None, rate >= 0, remainingRisk >= 0
                        if (rate >= 0 && direction != "None" && remainingRisk >= 0)
                        {
                            signalProducts.Add((productId, strategy, rate, direction));
                        }
                    }
                }
            }

            if (signalProducts.Count > 0)
            {
                Log("INFO", $"========== 满足条件的品种 ({signalProducts.Count}) ==========");
                foreach (var p in signalProducts)
                {
                    Log("INFO", $"{p.productId} | {p.strategy} | {p.rate:F6} | {p.direction}");
                }
                Log("INFO", "============================================");
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"记录信号数据异常: {ex.Message}");
        }
    }

    private bool IsInMonitorPeriod()
    {
        var configs = _databaseService.GetAllGDSignalConfigs();
        if (configs.Count == 0) return true;

        var config = configs[0];
        var now = DateTime.Now;
        var currentTime = now.TimeOfDay;

        // 检查日盘时间段
        if (TimeSpan.TryParse(config.MonitorStartTime, out var dayStart) &&
            TimeSpan.TryParse(config.MonitorEndTime, out var dayEnd))
        {
            if (currentTime >= dayStart && currentTime <= dayEnd)
            {
                return true;
            }
        }

        // 检查夜盘时间段
        if (config.MonitorNightSession)
        {
            if (TimeSpan.TryParse(config.NightSessionStartTime ?? "21:00", out var nightStart) &&
                TimeSpan.TryParse(config.NightSessionEndTime ?? "02:30", out var nightEnd))
            {
                if (nightStart > nightEnd)
                {
                    // 跨午夜
                    if (currentTime >= nightStart || currentTime <= nightEnd)
                    {
                        return true;
                    }
                }
                else
                {
                    if (currentTime >= nightStart && currentTime <= nightEnd)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private async Task<JToken?> FetchSignalDataAsync()
    {
        try
        {
            var messageSource = _databaseService.GetMessageSourceBySourceId("4");
            if (messageSource == null)
            {
                Log("ERROR", "未找到消息源 ID=4 的配置");
                return null;
            }

            var apiUrl = messageSource.ApiUrl;
            var allStrategys = new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" };

            var paramList = allStrategys.Select(s => $"Strategys={Uri.EscapeDataString(s)}").ToList();
            var fullUrl = apiUrl + (apiUrl.Contains('?') ? "&" : "?") + string.Join("&", paramList);

            Log("INFO", $"调用API: {fullUrl}");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrWhiteSpace(messageSource.ApiToken))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", messageSource.ApiToken);
            }

            var response = await client.GetAsync(fullUrl);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                Log("INFO", $"API返回: {content.Length} 字节");
                return JToken.Parse(content);
            }
            else
            {
                Log("ERROR", $"API错误: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"获取数据异常: {ex.Message}");
        }

        return null;
    }

    private string? BuildPushContent(JToken data, string? changeDescription = null)
    {
        var configs = _databaseService.GetAllGDSignalConfigs();
        if (configs.Count == 0) return null;

        var config = configs[0];

        var enabledStrategies = new List<string>();
        if (config.EnableGD15) enabledStrategies.Add("GD15");
        if (config.EnableGD20) enabledStrategies.Add("GD20");
        if (config.EnableGD25) enabledStrategies.Add("GD25");
        if (config.EnableGD30) enabledStrategies.Add("GD30");
        if (config.EnableGD35) enabledStrategies.Add("GD35");
        if (config.EnableGD40) enabledStrategies.Add("GD40");

        if (enabledStrategies.Count == 0)
        {
            enabledStrategies = new List<string> { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" };
        }

        var dataArray = data["data"] as JArray;
        if (dataArray == null) return null;

        // 收集满足条件的品种: direction != "None" && rate >= 0 && remainingRisk >= 0
        var strategyProducts = new Dictionary<string, List<(string productId, double rate, string direction)>>();
        foreach (var strategy in enabledStrategies)
        {
            strategyProducts[strategy] = new List<(string, double, string)>();
        }

        foreach (var productData in dataArray)
        {
            var productId = productData["productId"]?.ToString() ?? "";
            var items = productData["items"] as JObject;
            if (items == null) continue;

            // 收集所有策略的方向信息（满足基本筛选条件），用于GD15特殊筛选
            // GD15规则: 必须与全部6个策略(GD20~GD40)都有同方向持仓
            var allStrategies = new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" };
            var productDirections = new Dictionary<string, string>();
            foreach (var strategyName in allStrategies)
            {
                var strategyData = items[strategyName] as JObject;
                if (strategyData != null)
                {
                    var rate = strategyData["totalRealTimeStopPriceDiffRate"]?.Value<double>() ?? 0;
                    var direction = strategyData["direction"]?.ToString() ?? "";
                    var remainingRisk = strategyData["remainingRisk"]?.Value<double>() ?? 0;
                    // 与筛选条件一致: direction不能为None, rate >= 0, remainingRisk >= 0
                    if (rate >= 0 && direction != "None" && remainingRisk >= 0)
                    {
                        productDirections[strategyName] = direction;
                    }
                }
            }
            // 调试：输出该品种各策略的方向收集结果
            Log("DEBUG", $"[品种={productId}] 方向收集: {string.Join(", ", productDirections.Select(kv => $"{kv.Key}={kv.Value}"))}");

            foreach (var strategyName in enabledStrategies)
            {
                var strategyData = items[strategyName] as JObject;
                if (strategyData == null) continue;

                var rate = strategyData["totalRealTimeStopPriceDiffRate"]?.Value<double>() ?? 0;
                var direction = strategyData["direction"]?.ToString() ?? "";
                var remainingRisk = strategyData["remainingRisk"]?.Value<double>() ?? 0;
                // 筛选条件: direction不能为None, rate >= 0, remainingRisk >= 0
                if (rate >= 0 && direction != "None" && remainingRisk >= 0)
                {
                    // GD15特殊规则: 必须与全部策略(GD20~GD40)都有同方向持仓
                    if (strategyName == "GD15")
                    {
                        var allSameDirection = true;
                        var failedStrategies = new List<string>();
                        foreach (var otherStrategy in allStrategies.Where(s => s != "GD15"))
                        {
                            // 只有其他策略也满足基本筛选条件且方向一致才算
                            if (!productDirections.TryGetValue(otherStrategy, out var otherDir) || otherDir != direction)
                            {
                                allSameDirection = false;
                                failedStrategies.Add(otherStrategy);
                            }
                        }
                        if (!allSameDirection)
                        {
                            Log("DEBUG", $"[GD15筛选] 品种 {productId} 方向={direction} 被过滤，原因: {string.Join(",", failedStrategies)} 方向不一致或不满足筛选条件");
                            continue; // 跳过，不入选
                        }
                        Log("DEBUG", $"[GD15筛选] 品种 {productId} 方向={direction} 通过GD15同向持仓检查");
                    }
                    strategyProducts[strategyName].Add((productId, rate, direction));
                }
            }
        }

        // 按出现频率排序（频率高的在前）
        var productFrequency = new Dictionary<string, int>();
        foreach (var kvp in strategyProducts)
        {
            foreach (var product in kvp.Value)
            {
                if (productFrequency.ContainsKey(product.productId))
                    productFrequency[product.productId]++;
                else
                    productFrequency[product.productId] = 1;
            }
        }
        var sortedProducts = productFrequency.OrderByDescending(x => x.Value).Select(x => x.Key).ToList();

        // 调试日志：输出最终入选结果
        Log("DEBUG", $"========== GD15筛选最终结果 ==========");
        Log("DEBUG", $"GD15入选品种数: {strategyProducts["GD15"].Count}");
        foreach (var (pid, rate, dir) in strategyProducts["GD15"])
        {
            Log("DEBUG", $"  {pid} | {dir} | rate={rate:F6}");
        }
        Log("DEBUG", $"========================================");

        if (sortedProducts.Count == 0) return null;

        // 构建推送内容
        var isFirstPush = _lastPushData == null;
        var sb = new StringBuilder();

        sb.AppendLine($"📊 **盘中止损监测**");
        sb.AppendLine($"🕐 {DateTime.Now:yyyy-MM-dd HH:mm}");

        if (!isFirstPush && !string.IsNullOrEmpty(changeDescription) && changeDescription != "首次推送" && changeDescription != "无变化")
        {
            sb.AppendLine();
            sb.AppendLine("**🔄 变化详情:**");
            sb.Append(changeDescription);
        }

        if (isFirstPush)
        {
            // 首次推送，完整内容（按频率排序）
            sb.AppendLine();
            sb.AppendLine("**📈 满足条件的品种:**");
            // 按频率排序输出
            foreach (var productId in sortedProducts)
            {
                var productInfos = strategyProducts
                    .Where(kvp => kvp.Value.Any(p => p.productId == productId))
                    .Select(kvp => {
                        var p = kvp.Value.First(x => x.productId == productId);
                        return $"{kvp.Key}({p.direction}, {p.rate:F4})";
                    });
                sb.AppendLine($"• {productId}: {string.Join(", ", productInfos)} (出现{productFrequency[productId]}个策略)");
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("自动监测");

            return sb.ToString();
        }
        else
        {
            // 后续推送，差异对比
            var previousData = _lastPushData;
            var previousArray = previousData?["data"] as JArray;

            sb.AppendLine();
            sb.AppendLine("**🔄 变化品种:**");

            foreach (var strategy in enabledStrategies)
            {
                var currentProducts = strategyProducts[strategy];
                var previousProducts = new List<(string productId, double rate, string direction)>();

                if (previousArray != null)
                {
                    foreach (var productData in previousArray)
                    {
                        var productId = productData["productId"]?.ToString() ?? "";
                        var items = productData["items"] as JObject;
                        var strategyData = items?[strategy] as JObject;
                        if (strategyData != null)
                        {
                            var rate = strategyData["totalRealTimeStopPriceDiffRate"]?.Value<double>() ?? 0;
                            var direction = strategyData["direction"]?.ToString() ?? "";
                            var remainingRisk = strategyData["remainingRisk"]?.Value<double>() ?? 0;
                            if (rate >= 0 && direction != "None" && remainingRisk >= 0)
                            {
                                previousProducts.Add((productId, rate, direction));
                            }
                        }
                    }
                }

                var added = currentProducts.Where(cp => !previousProducts.Any(pp => pp.productId == cp.productId)).ToList();
                var removed = previousProducts.Where(pp => !currentProducts.Any(cp => cp.productId == pp.productId)).ToList();

                if (added.Count > 0 || removed.Count > 0)
                {
                    sb.AppendLine($"【{strategy}】");
                    if (added.Count > 0)
                    {
                        sb.AppendLine($"  ➕ 新增: {string.Join(", ", added.Select(a => $"{a.productId}({a.direction})"))}");
                    }
                    if (removed.Count > 0)
                    {
                        sb.AppendLine($"  ➖ 减少: {string.Join(", ", removed.Select(r => r.productId))}");
                    }
                }
            }

            // 如果没有新增或减少，返回null不推送
            if (!sb.ToString().Contains("新增") && !sb.ToString().Contains("减少"))
            {
                return null;
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("自动监测");

            return sb.ToString();
        }
    }

    private string ExtractSignalSummary(JToken data)
    {
        var sb = new StringBuilder();
        var dataArray = data["data"] as JArray;
        if (dataArray == null) return "";

        foreach (var productData in dataArray)
        {
            var productId = productData["productId"]?.ToString() ?? "";
            var items = productData["items"] as JObject;
            if (items == null) continue;

            foreach (var strategy in new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" })
            {
                var strategyData = items[strategy] as JObject;
                if (strategyData != null)
                {
                    var rate = strategyData["totalRealTimeStopPriceDiffRate"]?.Value<double>() ?? 0;
                    var direction = strategyData["direction"]?.ToString() ?? "";
                    var remainingRisk = strategyData["remainingRisk"]?.Value<double>() ?? 0;
                    // 过滤: direction不能为None, rate >= 0, remainingRisk >= 0
                    if (rate >= 0 && direction != "None" && remainingRisk >= 0)
                    {
                        sb.Append($"{productId}|{strategy}|{rate:F6}|{direction};");
                    }
                }
            }
        }
        return sb.ToString();
    }

    private void Log(string level, string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";

        // 异步更新UI，避免阻塞
        Dispatcher.BeginInvoke(() =>
        {
            LogTextBox.Text = entry + Environment.NewLine + LogTextBox.Text;
            // 限制日志行数，防止内存占用过多
            const int maxLines = 500;
            var lines = LogTextBox.Text.Split('\n');
            if (lines.Length > maxLines)
            {
                LogTextBox.Text = string.Join("\n", lines.Take(maxLines));
            }
            _logCallback?.Invoke(level, message);
        });

        lock (_logLock)
        {
            try
            {
                File.AppendAllText(_logFilePath, entry + Environment.NewLine);
            }
            catch { }
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Text = "";
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        BtnStop_Click(null, null);
        Close();
    }

    // 共享方法：获取过滤后的品种数据（应用GD15同向持仓规则）
    private Dictionary<string, List<(string productId, string direction)>> GetFilteredStrategyProducts(JArray dataArray, List<string>? enabledStrategies = null)
    {
        var allStrategies = new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" };
        var strategiesToUse = enabledStrategies?.Count > 0 ? enabledStrategies : allStrategies.ToList();

        var result = new Dictionary<string, List<(string productId, string direction)>>();
        foreach (var strategy in strategiesToUse)
        {
            result[strategy] = new List<(string, string)>();
        }

        foreach (var productData in dataArray)
        {
            var productId = productData["productId"]?.ToString() ?? "";
            var items = productData["items"] as JObject;
            if (items == null) continue;

            // 收集满足基本筛选条件的策略方向（用于当前策略筛选）
            var productDirections = new Dictionary<string, string>();
            // 收集所有持仓方向（不限条件，仅判断direction是否相同）
            var allPositions = new Dictionary<string, string>();
            foreach (var strategyName in allStrategies)
            {
                var strategyData = items[strategyName] as JObject;
                if (strategyData != null)
                {
                    var rate = strategyData["totalRealTimeStopPriceDiffRate"]?.Value<double>() ?? 0;
                    var direction = strategyData["direction"]?.ToString() ?? "";
                    var remainingRisk = strategyData["remainingRisk"]?.Value<double>() ?? 0;
                    // 基本筛选条件：rate >= 0, direction不是None, remainingRisk >= 0
                    if (rate >= 0 && direction != "None" && remainingRisk >= 0)
                    {
                        productDirections[strategyName] = direction;
                    }
                    // 所有持仓方向（不限条件）
                    if (direction != "None" && !string.IsNullOrEmpty(direction))
                    {
                        allPositions[strategyName] = direction;
                    }
                }
            }

            foreach (var strategy in strategiesToUse)
            {
                var strategyData = items[strategy] as JObject;
                if (strategyData == null) continue;

                var rate = strategyData["totalRealTimeStopPriceDiffRate"]?.Value<double>() ?? 0;
                var direction = strategyData["direction"]?.ToString() ?? "";
                var remainingRisk = strategyData["remainingRisk"]?.Value<double>() ?? 0;
                if (rate >= 0 && direction != "None" && remainingRisk >= 0)
                {
                    // 判断更高策略是否同向持仓（不限条件，仅比较direction）
                    var higherStrategies = GetHigherStrategies(strategy, allStrategies);
                    if (higherStrategies.Length > 0)
                    {
                        var allHigherMatch = higherStrategies.All(hs =>
                            allPositions.TryGetValue(hs, out var higherDir) && higherDir == direction);
                        if (!allHigherMatch) continue;
                    }
                    result[strategy].Add((productId, direction));
                }
            }
        }
        return result;
    }

    static string[] GetHigherStrategies(string strategy, string[] allStrats)
    {
        return strategy switch
        {
            "GD15" => new[] { "GD20", "GD25", "GD30", "GD35", "GD40" },
            "GD20" => new[] { "GD25", "GD30", "GD35", "GD40" },
            "GD25" => new[] { "GD30", "GD35", "GD40" },
            "GD30" => new[] { "GD35", "GD40" },
            "GD35" => new[] { "GD40" },
            "GD40" => Array.Empty<string>(),
            _ => Array.Empty<string>()
        };
    }

    // 根据过滤后的数据构建变化描述
    private string BuildChangeDescriptionFromFiltered(
        Dictionary<string, List<(string productId, string direction)>> currentFiltered,
        Dictionary<string, List<(string productId, string direction)>>? previousFiltered,
        out List<(string strategy, string productId, bool isAdded)> changedProducts)
    {
        changedProducts = new List<(string, string, bool)>();
        var sb = new StringBuilder();
        var allStrategies = new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" };

        foreach (var strategy in allStrategies)
        {
            var added = currentFiltered[strategy].Where(cp => previousFiltered == null || !previousFiltered[strategy].Any(pp => pp.productId == cp.productId)).ToList();
            var removed = previousFiltered?[strategy].Where(pp => !currentFiltered[strategy].Any(cp => cp.productId == pp.productId)).ToList() ?? new List<(string, string)>();

            // 记录变化的产品
            foreach (var a in added)
            {
                changedProducts.Add((strategy, a.productId, true));
            }
            foreach (var r in removed)
            {
                changedProducts.Add((strategy, r.productId, false));
            }

            if (added.Count > 0 || removed.Count > 0)
            {
                sb.AppendLine($"【{strategy}】");
                if (added.Count > 0)
                {
                    sb.AppendLine($"  ➕ 新增: {string.Join(", ", added.Select(a => $"{a.productId}({a.direction})"))}");
                }
                if (removed.Count > 0)
                {
                    sb.AppendLine($"  ➖ 减少: {string.Join(", ", removed.Select(r => $"{r.productId}({r.direction})"))}");
                }
            }
        }

        return sb.Length > 0 ? sb.ToString() : "无变化";
    }

    // 防闪烁检查：如果品种在5分钟内状态发生过变化，但现在又回到了之前的状态，则忽略这次变化
    private List<(string strategy, string productId, bool isAdded)> FilterFlickerChanges(
        List<(string strategy, string productId, bool isAdded)> changedProducts,
        Dictionary<string, List<(string productId, string direction)>> currentFiltered,
        Dictionary<string, List<(string productId, string direction)>>? previousFiltered)
    {
        var filteredChanges = new List<(string strategy, string productId, bool isAdded)>();
        var now = DateTime.Now;

        foreach (var change in changedProducts)
        {
            var cacheKey = $"{change.strategy}:{change.productId}";
            bool shouldInclude = true;

            lock (_flickerLock)
            {
                if (_flickerCache.TryGetValue(cacheKey, out var lastChangeTime))
                {
                    if ((now - lastChangeTime).TotalMinutes < FlickerCacheMinutes)
                    {
                        // 品种在5分钟内发生过变化，检查当前状态是否与缓存时相同
                        bool inCurrent = currentFiltered.ContainsKey(change.strategy) &&
                                         currentFiltered[change.strategy].Any(p => p.productId == change.productId);
                        bool inPrevious = previousFiltered?.ContainsKey(change.strategy) == true &&
                                          previousFiltered[change.strategy].Any(p => p.productId == change.productId);

                        // 如果当前状态与上次变化时的状态相同（都是新增或都是减少），则跳过
                        if ((change.isAdded && inCurrent) || (!change.isAdded && !inCurrent))
                        {
                            Log("DEBUG", $"[防闪烁] 品种 {change.productId} 在{FlickerCacheMinutes - (int)(now - lastChangeTime).TotalMinutes}分钟内状态重复，跳过");
                            shouldInclude = false;
                        }
                    }
                }
            }

            if (shouldInclude)
            {
                filteredChanges.Add(change);
                // 记录这次变化
                lock (_flickerLock)
                {
                    _flickerCache[cacheKey] = now;
                }
            }
        }

        return filteredChanges;
    }

    // 根据过滤后的变化列表构建变化描述文本
    private string BuildFilteredChangeText(
        List<(string strategy, string productId, bool isAdded)> filteredChanges,
        Dictionary<string, List<(string productId, string direction)>> currentFiltered,
        Dictionary<string, List<(string productId, string direction)>>? previousFiltered)
    {
        if (filteredChanges.Count == 0) return "";

        var sb = new StringBuilder();
        var allStrategies = new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" };

        foreach (var strategy in allStrategies)
        {
            var strategyChanges = filteredChanges.Where(c => c.strategy == strategy).ToList();
            if (strategyChanges.Count == 0) continue;

            var added = strategyChanges.Where(c => c.isAdded).ToList();
            var removed = strategyChanges.Where(c => !c.isAdded).ToList();

            sb.AppendLine($"【{strategy}】");
            if (added.Count > 0)
            {
                var addedDetails = added.Select(a =>
                {
                    var item = currentFiltered[strategy].FirstOrDefault(p => p.productId == a.productId);
                    return item.productId != null ? $"{item.productId}({item.direction})" : a.productId;
                });
                sb.AppendLine($"  ➕ 新增: {string.Join(", ", addedDetails)}");
            }
            if (removed.Count > 0)
            {
                sb.AppendLine($"  ➖ 减少: {string.Join(", ", removed.Select(r => r.productId))}");
            }
        }

        return sb.ToString();
    }

    private string BuildChangeDescription(JToken currentData, JToken? previousData)
    {
        if (previousData == null) return "首次推送";

        var sb = new StringBuilder();
        var currentArray = currentData["data"] as JArray;
        var previousArray = previousData?["data"] as JArray;
        var allStrategies = new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" };

        if (currentArray == null) return "无变化";

        // 使用共享方法获取过滤后的数据
        var currentFiltered = GetFilteredStrategyProducts(currentArray);
        var previousFiltered = previousArray != null ? GetFilteredStrategyProducts(previousArray) : null;

        foreach (var strategy in allStrategies)
        {
            var added = currentFiltered[strategy].Where(cp => previousFiltered == null || !previousFiltered[strategy].Any(pp => pp.productId == cp.productId)).ToList();
            var removed = previousFiltered?[strategy].Where(pp => !currentFiltered[strategy].Any(cp => cp.productId == pp.productId)).ToList() ?? new List<(string, string)>();

            if (added.Count > 0 || removed.Count > 0)
            {
                sb.AppendLine($"【{strategy}】");
                if (added.Count > 0)
                {
                    sb.AppendLine($"  ➕ 新增: {string.Join(", ", added.Select(a => $"{a.productId}({a.direction})"))}");
                }
                if (removed.Count > 0)
                {
                    sb.AppendLine($"  ➖ 减少: {string.Join(", ", removed.Select(r => $"{r.productId}({r.direction})"))}");
                }
            }
        }

        return sb.Length > 0 ? sb.ToString() : "无变化";
    }

    private async Task<string?> GenerateSignalImageAsync(JToken data)
    {
        try
        {
            var dataArray = data["data"] as JArray;
            if (dataArray == null) return null;

            // 获取已勾选的策略列表
            var enabledStrategies = GetEnabledStrategies();
            if (enabledStrategies.Count == 0)
            {
                Log("WARN", "没有勾选任何策略，无法生成图片");
                return null;
            }

            // 收集每个策略对应的品种列表（按列填充）
            var strategyProducts = new Dictionary<string, List<(string productId, string direction)>>();
            foreach (var strategy in enabledStrategies)
            {
                strategyProducts[strategy] = new List<(string, string)>();
            }

            // 收集每个品种在各策略中的持仓方向（不限制止损价差，只要有持仓就算）
            var allStrategies = enabledStrategies.ToArray();

            foreach (var productData in dataArray)
            {
                var productId = productData["productId"]?.ToString() ?? "";
                var items = productData["items"] as JObject;
                if (items == null) continue;

            // 收集满足基本筛选条件的策略方向（用于当前策略筛选）
            var productDirections = new Dictionary<string, string>();
            // 收集所有持仓方向（不限条件，仅判断direction是否相同）
            var allPositions = new Dictionary<string, string>();
            foreach (var strategyName in allStrategies)
            {
                var strategyData = items[strategyName] as JObject;
                if (strategyData != null)
                {
                    var rate = strategyData["totalRealTimeStopPriceDiffRate"]?.Value<double>() ?? 0;
                    var direction = strategyData["direction"]?.ToString() ?? "";
                    var remainingRisk = strategyData["remainingRisk"]?.Value<double>() ?? 0;
                    // 基本筛选条件：rate >= 0, direction不是None, remainingRisk >= 0
                    if (rate >= 0 && direction != "None" && remainingRisk >= 0)
                    {
                        productDirections[strategyName] = direction;
                    }
                    // 所有持仓方向（不限条件）
                    if (direction != "None" && !string.IsNullOrEmpty(direction))
                    {
                        allPositions[strategyName] = direction;
                    }
                }
            }

            // 对每个策略进行筛选
            foreach (var strategy in allStrategies)
                {
                    var strategyData = items[strategy] as JObject;
                    if (strategyData == null) continue;

                    var rate = strategyData["totalRealTimeStopPriceDiffRate"]?.Value<double>() ?? 0;
                    var direction = strategyData["direction"]?.ToString() ?? "";
                    var remainingRisk = strategyData["remainingRisk"]?.Value<double>() ?? 0;

                    // 基本筛选条件: direction不能为None, rate >= 0, remainingRisk >= 0
                    if (rate >= 0 && direction != "None" && remainingRisk >= 0)
                    {
                        // 判断更高策略是否同向持仓（不限条件，仅比较direction）
                        var enabledSet = new HashSet<string>(enabledStrategies);
                        var higherStrategies = GetHigherStrategies(strategy, allStrategies, enabledSet);
                        if (higherStrategies.Length > 0)
                        {
                            var allHigherMatch = higherStrategies.All(hs =>
                                allPositions.TryGetValue(hs, out var higherDir) && higherDir == direction);
                            if (!allHigherMatch) continue;
                        }

                        strategyProducts[strategy].Add((productId, direction));
                    }
                }
            }

            // 辅助方法：获取比当前策略更高的策略列表（只返回已勾选的更高策略）
            static string[] GetHigherStrategies(string strategy, string[] allStrats, HashSet<string> enabledSet)
            {
                var baseResult = strategy switch
                {
                    "GD15" => new[] { "GD20", "GD25", "GD30", "GD35", "GD40" },
                    "GD20" => new[] { "GD25", "GD30", "GD35", "GD40" },
                    "GD25" => new[] { "GD30", "GD35", "GD40" },
                    "GD30" => new[] { "GD35", "GD40" },
                    "GD35" => new[] { "GD40" },
                    "GD40" => Array.Empty<string>(),
                    _ => Array.Empty<string>()
                };
                // 过滤：只返回已勾选的更高策略
                return baseResult.Where(s => enabledSet.Contains(s)).ToArray();
            }

            // 计算品种出现频率并排序
            var productFrequency = new Dictionary<string, int>();
            foreach (var kvp in strategyProducts)
            {
                foreach (var (productId, _) in kvp.Value)
                {
                    if (productFrequency.ContainsKey(productId))
                        productFrequency[productId]++;
                    else
                        productFrequency[productId] = 1;
                }
            }
            var sortedProducts = productFrequency.OrderByDescending(x => x.Value).ToList();

            // 检查是否有数据
            if (sortedProducts.Count == 0) return null;

            // 构建排序后的品种数据：按品种频率重新组织每列
            var strategies = enabledStrategies.ToArray();
            var sortedStrategyProducts = new Dictionary<string, List<(string productId, string direction)>>();
            foreach (var strategy in strategies)
            {
                sortedStrategyProducts[strategy] = new List<(string, string)>();
            }

            // 按频率排序遍历品种
            foreach (var (productId, _) in sortedProducts)
            {
                foreach (var strategy in strategies)
                {
                    var originalList = strategyProducts[strategy];
                    var product = originalList.FirstOrDefault(p => p.productId == productId);
                    if (product != default)
                    {
                        sortedStrategyProducts[strategy].Add(product);
                    }
                }
            }

            // 图片参数：策略名 + 品种列表 = 最多 8 列
            int colCount = 1 + strategies.Length; // 策略名 + 6个策略
            int maxRows = sortedStrategyProducts.Values.Max(list => list.Count);
            if (maxRows == 0) return null;

            int cellHeight = 28;
            int titleHeight = 36;
            int rowNumColWidth = 40;
            int padding = 20;
            int headerHeight = 50;

            // 先测量字体，确定列宽
            using var titleFont = new Font(System.Drawing.FontFamily.GenericSansSerif, 14, System.Drawing.FontStyle.Bold);
            using var headerFont = new Font(System.Drawing.FontFamily.GenericSansSerif, 11, System.Drawing.FontStyle.Bold);
            using var cellFont = new Font(System.Drawing.FontFamily.GenericMonospace, 10);

            // 用临时bitmap测量文字宽度（需要graphics对象）
            using var tempBitmap = new Bitmap(1, 1);
            using var tempG = Graphics.FromImage(tempBitmap);
            tempG.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // 计算每列宽度：基于品种名实际宽度 + 策略名宽度
            var colWidths = new List<int> { rowNumColWidth }; // 第一列是行号
            foreach (var strategy in strategies)
            {
                var headerW = tempG.MeasureString(strategy, headerFont).Width + 16;
                colWidths.Add((int)Math.Ceiling(headerW));
            }

            // 动态调整策略列宽度：取该列中最长品种名的宽度
            for (int i = 0; i < strategies.Length; i++)
            {
                var productList = sortedStrategyProducts[strategies[i]];
                if (productList.Count > 0)
                {
                    var maxProductW = productList.Max(p => tempG.MeasureString(p.productId, cellFont).Width);
                    var targetW = (int)Math.Ceiling(maxProductW) + 16;
                    if (targetW > colWidths[i + 1])
                        colWidths[i + 1] = targetW;
                }
            }

            // 首次或不显示变化信息时，changeInfoAreaHeight为0
            int changeInfoAreaHeight = 0;
            int imgHeight = padding * 2 + headerHeight + titleHeight + cellHeight * maxRows + 10 + changeInfoAreaHeight;

            // 计算图片宽度：基于所有列的宽度之和
            int imgWidth = padding * 2 + colWidths.Sum();

            using var bitmap = new Bitmap(imgWidth, imgHeight);
            using var g = Graphics.FromImage(bitmap);

            // 背景
            g.Clear(Color.FromArgb(30, 30, 30));
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using var titleBrush = new SolidBrush(Color.White);
            using var headerBrush = new SolidBrush(Color.FromArgb(100, 180, 255));
            using var gridPen = new Pen(Color.FromArgb(80, 80, 80), 1);

            var titleText = $"趋势品种池 {DateTime.Now:yyyy-MM-dd HH:mm}";
            var titleSize = g.MeasureString(titleText, titleFont);
            g.DrawString(titleText, titleFont, titleBrush, (imgWidth - titleSize.Width) / 2, padding);

            // 绘制表头
            int y = padding + headerHeight;
            int x = padding;
            for (int col = 0; col < colCount; col++)
            {
                var cw = colWidths[col];
                g.FillRectangle(new SolidBrush(Color.FromArgb(50, 50, 50)), x, y, cw, titleHeight);
                g.DrawRectangle(gridPen, x, y, cw, titleHeight);
                string headerText = col == 0 ? "" : strategies[col - 1];
                if (!string.IsNullOrEmpty(headerText))
                {
                    var headerSize = g.MeasureString(headerText, headerFont);
                    g.DrawString(headerText, headerFont, headerBrush, x + (cw - headerSize.Width) / 2, y + (titleHeight - headerSize.Height) / 2);
                }
                x += cw;
            }

            // 绘制数据列
            y += titleHeight;
            for (int row = 0; row < maxRows; row++)
            {
                x = padding;

                // 收集当前行的所有品种（用于判断是否跨列重复）
                var rowProducts = new Dictionary<int, string>(); // colIndex -> productId
                for (int col = 0; col < strategies.Length; col++)
                {
                    var products = sortedStrategyProducts[strategies[col]];
                    if (row < products.Count)
                    {
                        rowProducts[col] = products[row].productId;
                    }
                }

                // 统计每个品种在当前行出现的次数
                var productCounts = rowProducts.Values.GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count());
                var repeatedProducts = productCounts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToHashSet();

                // 左侧行号
                g.FillRectangle(new SolidBrush(Color.FromArgb(40, 40, 40)), x, y, rowNumColWidth, cellHeight);
                g.DrawRectangle(gridPen, x, y, rowNumColWidth, cellHeight);
                var rowText = (row + 1).ToString();
                var rowSize = g.MeasureString(rowText, cellFont);
                g.DrawString(rowText, cellFont, titleBrush, x + (rowNumColWidth - rowSize.Width) / 2, y + (cellHeight - rowSize.Height) / 2);
                x += rowNumColWidth;

                for (int col = 0; col < strategies.Length; col++)
                {
                    var cw = colWidths[col + 1];
                    var products = sortedStrategyProducts[strategies[col]];
                    var isRepeated = false;
                    if (row < products.Count && repeatedProducts.Contains(products[row].productId))
                    {
                        isRepeated = true;
                    }
                    // 重复品种使用深灰色背景
                    if (isRepeated)
                        g.FillRectangle(new SolidBrush(Color.FromArgb(60, 60, 65)), x, y, cw, cellHeight);
                    else
                        g.FillRectangle(new SolidBrush(Color.FromArgb(35, 35, 35)), x, y, cw, cellHeight);
                    g.DrawRectangle(gridPen, x, y, cw, cellHeight);

                    if (row < products.Count)
                    {
                        var (pId, direction) = products[row];
                        var color = direction.ToLower() == "long" ? Color.FromArgb(220, 80, 80) :
                                    direction.ToLower() == "short" ? Color.FromArgb(80, 200, 120) :
                                    Color.White;
                        using var productBrush = new SolidBrush(color);
                        var pSize = g.MeasureString(pId, cellFont);
                        // 文字居中对齐
                        float drawX = x + (cw - pSize.Width) / 2;
                        float drawY = y + (cellHeight - pSize.Height) / 2;
                        if (pSize.Width > cw - 8)
                        {
                            // 缩放字体适应列宽
                            float scale = (cw - 8) / pSize.Width;
                            using var scaledFont = new Font(System.Drawing.FontFamily.GenericMonospace, 10 * scale);
                            var scaledSize = g.MeasureString(pId, scaledFont);
                            g.DrawString(pId, scaledFont, productBrush, x + (cw - scaledSize.Width) / 2, drawY);
                        }
                        else
                        {
                            g.DrawString(pId, cellFont, productBrush, drawX, drawY);
                        }
                    }
                    x += cw;
                }
                y += cellHeight;
            }

            // 保存图片
            var imagePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"signal_{DateTime.Now:yyyyMMddHHmmss}.png");
            bitmap.Save(imagePath, ImageFormat.Png);
            Log("INFO", $"图片已生成: {imagePath}");
            return imagePath;
        }
        catch (Exception ex)
        {
            Log("ERROR", $"生成图片异常: {ex.Message}");
            return null;
        }
    }

    // 文字换行辅助方法
    private List<string> WrapText(Graphics g, string text, Font font, float maxWidth)
    {
        var lines = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            sb.Append(ch);
            if (g.MeasureString(sb.ToString(), font).Width > maxWidth)
            {
                // 回退最后一个字符到下一行
                sb.Length--;
                if (sb.Length > 0)
                {
                    lines.Add(sb.ToString());
                    sb.Clear();
                    sb.Append(ch);
                }
            }
        }
        if (sb.Length > 0)
            lines.Add(sb.ToString());
        return lines;
    }

    private async Task<bool> PushImageToFeishuAsync(string imagePath)
    {
        try
        {
            if (_selectedTerminalIds.Count == 0) return false;

            var terminals = _databaseService.GetAllTerminalConfigs();
            var successCount = 0;
            var failCount = 0;

            foreach (var terminalId in _selectedTerminalIds)
            {
                var terminal = terminals.FirstOrDefault(t => t.TerminalId == terminalId);
                if (terminal == null || string.IsNullOrEmpty(terminal.ImageApiKey) || string.IsNullOrEmpty(terminal.ImageSecretKey))
                {
                    Log("ERROR", $"终端 {terminalId} 的图片推送配置不完整（需要 ImageApiKey 和 ImageSecretKey）");
                    failCount++;
                    continue;
                }

                // 获取 AccessToken
                var token = await GetTenantAccessTokenAsync(terminal.ImageApiKey, terminal.ImageSecretKey);
                if (string.IsNullOrEmpty(token))
                {
                    Log("ERROR", $"终端 {terminalId} 获取 AccessToken 失败");
                    failCount++;
                    continue;
                }

                // 上传图片获取 image_key
                var imageKey = await UploadImageDataAsync(imagePath, token);
                if (string.IsNullOrEmpty(imageKey))
                {
                    Log("ERROR", $"终端 {terminalId} 上传图片失败");
                    failCount++;
                    continue;
                }

                Log("INFO", $"图片上传成功，imageKey: {imageKey}");

                // 发送图片消息
                var receiverId = terminal.ImageReceiverId;
                var idType = receiverId.StartsWith("ou_") ? "open_id" : "chat_id";
                var sendUrl = $"https://open.feishu.cn/open-apis/im/v1/messages?receive_id_type={idType}";

                var contentObj = new { image_key = imageKey };
                var payload = new
                {
                    receive_id = receiverId,
                    msg_type = "image",
                    content = Newtonsoft.Json.JsonConvert.SerializeObject(contentObj)
                };

                var request = new HttpRequestMessage(HttpMethod.Post, sendUrl);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                using var client = new HttpClient();
                var response = await client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    successCount++;
                    Log("INFO", $"✅ 图片推送至 {terminalId} 成功");
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Log("ERROR", $"❌ 图片推送至 {terminalId} 失败: {response.StatusCode} - {errorContent}");
                    failCount++;
                }
            }

            if (successCount > 0)
            {
                Log("INFO", $"✅ 图片推送完成: 成功 {successCount}, 失败 {failCount}");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log("ERROR", $"❌ 图片推送异常: {ex.Message}");
            return false;
        }
    }

    private async Task<string?> GetTenantAccessTokenAsync(string apiKey, string secretKey)
    {
        try
        {
            using var client = new HttpClient();
            var payload = new { app_id = apiKey, app_secret = secretKey };
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal", httpContent);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var result = Newtonsoft.Json.JsonConvert.DeserializeObject<JObject>(content);
                return result?["tenant_access_token"]?.ToString();
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"获取 AccessToken 异常: {ex.Message}");
        }
        return null;
    }

    private async Task<string?> UploadImageDataAsync(string imagePath, string token)
    {
        try
        {
            using var client = new HttpClient();
            using var form = new MultipartFormDataContent();

            var imageBytes = await File.ReadAllBytesAsync(imagePath);
            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

            form.Add(imageContent, "image", Path.GetFileName(imagePath));
            form.Add(new StringContent("message"), "image_type");

            var request = new HttpRequestMessage(HttpMethod.Post, "https://open.feishu.cn/open-apis/im/v1/images");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Content = form;

            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var result = Newtonsoft.Json.JsonConvert.DeserializeObject<JObject>(content);
                return result?["data"]?["image_key"]?.ToString();
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Log("ERROR", $"上传图片失败: {response.StatusCode} - {errorContent}");
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"上传图片异常: {ex.Message}");
        }
        return null;
    }

    private async Task<bool> PushTextToFeishuAsync(string content)
    {
        try
        {
            if (_selectedTerminalIds.Count == 0) return false;

            var terminals = _databaseService.GetAllTerminalConfigs();
            var successCount = 0;
            var failCount = 0;

            foreach (var terminalId in _selectedTerminalIds)
            {
                var terminal = terminals.FirstOrDefault(t => t.TerminalId == terminalId);
                if (terminal == null || string.IsNullOrEmpty(terminal.TextWebhook))
                {
                    Log("ERROR", $"终端 {terminalId} 的 Webhook 未配置");
                    failCount++;
                    continue;
                }

                var message = new
                {
                    msg_type = "text",
                    content = new { text = content }
                };

                using var client = new HttpClient();
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(message);
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(terminal.TextWebhook, httpContent);

                if (response.IsSuccessStatusCode)
                {
                    successCount++;
                }
                else
                {
                    Log("ERROR", $"❌ 推送至 {terminalId} 失败: {response.StatusCode}");
                    failCount++;
                }
            }

            if (successCount > 0)
            {
                Log("INFO", $"✅ 文字推送完成: 成功 {successCount}, 失败 {failCount}");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log("ERROR", $"❌ 推送异常: {ex.Message}");
            return false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _monitorTimer?.Dispose();
        _monitorTimer = null;
        _isMonitoring = false;
        base.OnClosed(e);
    }
}
