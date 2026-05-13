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
    private List<string> _tableColumns = new();
    private readonly string _columnsFilePath;
    private const int FlickerCacheMinutes = 5;
    private System.Windows.Threading.DispatcherTimer? _uiTimer;
    private System.Windows.Threading.DispatcherTimer? _heartbeatTimer;
    private DateTime? _lastHeartbeatSent;
    private readonly string _historyFilePath;
    private readonly string _debugFilePath;

    public GDStopLossMonitorWindow(DatabaseService databaseService, ConfigService configService, Action<string, string> logCallback)
    {
        InitializeComponent();

        _databaseService = databaseService;
        _configService = configService;
        _logCallback = logCallback;

        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        _logFilePath = Path.Combine(appDir, $"stop_loss_monitor_{DateTime.Now:yyyyMMdd}.log");

        // 历史数据保存在固定目录，避免随安装路径变化
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InfoTransfer");
        Directory.CreateDirectory(dataDir);
        _historyFilePath = Path.Combine(dataDir, "stop_loss_history.json");
        _columnsFilePath = Path.Combine(dataDir, "stop_loss_columns.json");
        _debugFilePath = Path.Combine(dataDir, $"stop_loss_debug_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        // 绑定事件
        ChkSendText.Checked += ChkSendOption_Changed;
        ChkSendText.Unchecked += ChkSendOption_Changed;
        ChkSendImage.Checked += ChkSendOption_Changed;
        ChkSendImage.Unchecked += ChkSendOption_Changed;

        // 加载历史数据
        LoadHistoryData();

        // 加载列信息
        if (File.Exists(_columnsFilePath))
        {
            try
            {
                var colsJson = File.ReadAllText(_columnsFilePath);
                _tableColumns = System.Text.Json.JsonSerializer.Deserialize<List<string>>(colsJson) ?? new List<string>();
            }
            catch { }
        }

        LoadTerminals();
        LoadSendOptions();
    }

    // 加载历史数据
    private void LoadHistoryData()
    {
        try
        {
            if (File.Exists(_historyFilePath))
            {
                var json = File.ReadAllText(_historyFilePath);
                _lastPushData = JToken.Parse(json);
                Log("INFO", "已加载历史数据用于比对");
            }
        }
        catch (Exception ex)
        {
            Log("WARN", $"加载历史数据失败: {ex.Message}");
        }
    }

    // 加载历史数据用于比对
    private Dictionary<string, List<(string productId, string direction, double remainingRisk)>>? LoadHistoryDataForCompare()
    {
        try
        {
            if (!File.Exists(_historyFilePath))
                return null;

            var json = File.ReadAllText(_historyFilePath);
            var result = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, List<Dictionary<string, string>>>>(json);

            if (result == null) return null;

            var dict = new Dictionary<string, List<(string productId, string direction, double remainingRisk)>>();
            foreach (var kvp in result)
            {
                var products = kvp.Value
                    .Where(d => d.ContainsKey("productId") && !string.IsNullOrEmpty(d["productId"]))
                    .Select(d => (d["productId"], d.GetValueOrDefault("direction") ?? "", d.GetValueOrDefault("remainingRisk") != null ? double.Parse(d["remainingRisk"]) : 0.0))
                    .ToList();
                dict[kvp.Key] = products;
            }

            Log("DEBUG", $"[历史数据] 已加载历史比对数据，共{dict.Sum(kv => kv.Value.Count)}条记录");
            return dict;
        }
        catch (Exception ex)
        {
            Log("WARN", $"加载历史数据用于比对失败: {ex.Message}");
            return null;
        }
    }

    // 保存历史数据 - 使用 Dictionary<string, List<Dictionary<string, string>>> 格式便于读取
    private void SaveHistoryData(Dictionary<string, List<(string productId, string direction, double remainingRisk)>> filteredData)
    {
        try
        {
            // 转换为便于 JSON 序列化/反序列化的格式
            var saveData = filteredData.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Select(p => new Dictionary<string, string> { ["productId"] = p.productId, ["direction"] = p.direction, ["remainingRisk"] = p.remainingRisk.ToString() }).ToList()
            );
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(saveData, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(_historyFilePath, json);
        }
        catch (Exception ex)
        {
            Log("WARN", $"保存历史数据失败: {ex.Message}");
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _uiTimer?.Stop();
        _uiTimer = null;
        _heartbeatTimer?.Stop();
        _heartbeatTimer = null;
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

        // 使用 DispatcherTimer（确保在UI线程上执行）
        _uiTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(_scanIntervalMinutes)
        };
        _uiTimer.Tick += async (s, e) => await ExecuteMonitorAsync();
        _uiTimer.Start();

        // 启动心跳定时器（每分钟检查一次整点）
        _heartbeatTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _heartbeatTimer.Tick += async (s, e) => await CheckAndSendHeartbeatAsync();
        _heartbeatTimer.Start();
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _isMonitoring = false;
        _uiTimer?.Stop();
        _uiTimer = null;
        _heartbeatTimer?.Stop();
        _heartbeatTimer = null;

        BtnStart.IsEnabled = true;
        BtnStop.IsEnabled = false;
        TxtStatus.Text = "状态: 已停止";
        TxtNextRun.Text = "下次执行: --";

        Log("INFO", "========== 盘中止损监测已停止 ==========");
    }

    // 检查并发送心跳（交易日9:00和15:00发送存活通知）
    private async Task CheckAndSendHeartbeatAsync()
    {
        if (!_isMonitoring) return;

        var now = DateTime.Now;

        // 检查是否是周末（周六=6，周日=0）
        if (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday)
        {
            return;
        }

        // 检查是否是节假日
        if (IsHoliday(now)) return;

        // 只在9:00和15:00整点发送
        if (now.Minute != 0 || (now.Hour != 9 && now.Hour != 15))
        {
            return;
        }

        // 检查是否已经发送过（避免重复）
        if (_lastHeartbeatSent.HasValue && _lastHeartbeatSent.Value.Date == now.Date && _lastHeartbeatSent.Value.Hour == now.Hour)
        {
            return;
        }

        // 发送心跳
        var message = $"✅ 监控工作正常运行中 | {now:yyyy-MM-dd HH:mm:ss}";
        var success = await PushTextToFeishuAsync(message);
        if (success)
        {
            _lastHeartbeatSent = now;
            Log("INFO", $"存活通知已发送 ({now:HH}:00)");
        }
        else
        {
            Log("WARN", $"存活通知发送失败 ({now:HH}:00)");
        }
    }

    // 判断是否节假日（简化实现，可根据需要扩展）
    private bool IsHoliday(DateTime date)
    {
        var holidays = new HashSet<string>
        {
            "2026-01-01", "2026-02-15", "2026-02-16", "2026-02-17", "2026-02-18", "2026-02-19", "2026-02-20", "2026-02-21",
            "2026-04-04", "2026-04-05", "2026-04-06",
            "2026-05-01", "2026-05-02", "2026-05-03",
            "2026-06-20", "2026-06-21", "2026-06-22",
            "2026-09-25", "2026-09-26", "2026-09-27",
            "2026-10-01", "2026-10-02", "2026-10-03", "2026-10-04", "2026-10-05", "2026-10-06", "2026-10-07", "2026-10-08",
        };
        return holidays.Contains(date.ToString("yyyy-MM-dd"));
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
                Log("INFO", "不在监控时间段内，跳过");
                return;
            }

            // 获取API数据
            var data = await FetchSignalDataAsync();
            if (data == null)
            {
                Log("ERROR", "获取数据失败");
                return;
            }

            // 获取本次符合条件的品种列表（使用已勾选的策略）
            var enabledStrategies = GetEnabledStrategies();
            var currentFiltered = GetFilteredStrategyProducts(data, enabledStrategies);

            // 判断是否首次推送或数据有变化
            var isFirstPush = _lastPushData == null;
            var hasChanged = false;
            var changeDescription = "";
            var changedProducts = new List<(string strategy, string productId, bool isAdded)>();

            // 尝试加载历史数据进行比对
            var historyFiltered = LoadHistoryDataForCompare();
            var previousFiltered = historyFiltered ?? (_lastPushData != null ? GetFilteredStrategyProducts(_lastPushData, enabledStrategies) : null);

            if (isFirstPush)
            {
                if (previousFiltered != null)
                {
                    // 与历史数据比对
                    changeDescription = BuildChangeDescriptionFromFiltered(currentFiltered, previousFiltered, out changedProducts);
                    hasChanged = !string.IsNullOrEmpty(changeDescription) && changeDescription != "无变化";
                }

                if (!hasChanged)
                {
                    Dispatcher.Invoke(() =>
                    {
                        TxtLastResult.Text = $"上次结果: {DateTime.Now:HH:mm:ss} - 与历史相同";
                    });
                    // 保存过滤后的数据
                    SaveHistoryData(currentFiltered);
                    return;
                }

                Log("INFO", "数据有变化，开始发送");
            }
            else
            {
                changeDescription = BuildChangeDescriptionFromFiltered(currentFiltered, previousFiltered, out changedProducts);
                hasChanged = !string.IsNullOrEmpty(changeDescription) && changeDescription != "无变化";

                // 防闪烁检查
                if (hasChanged && changedProducts.Count > 0)
                {
                    var filteredChanges = FilterFlickerChanges(changedProducts, currentFiltered, previousFiltered);
                    if (filteredChanges.Count == 0)
                    {
                        Log("INFO", "变化被防闪烁过滤，跳过");
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

                        // 非首次推送且有变化内容时，发送文字消息
                        if (sendText && !isFirstPush && !string.IsNullOrEmpty(changeDescription))
                        {
                            var textContent = BuildPushContent(data, changeDescription);
                            if (textContent != null)
                            {
                                await PushTextToFeishuAsync(textContent);
                            }
                        }

                        // 更新参考数据
                        _lastPushData = data.DeepClone();
                        SaveHistoryData(currentFiltered);
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
                        _lastPushData = data.DeepClone();
                        SaveHistoryData(currentFiltered);
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
            var errorMsg = ex.Message;
            if (ex.InnerException != null)
            {
                errorMsg += $" (Inner: {ex.InnerException.Message})";
            }
            Log("ERROR", $"执行监测异常: {errorMsg}");
            try
            {
                Dispatcher.Invoke(() =>
                {
                    TxtLastResult.Text = $"上次结果: {DateTime.Now:HH:mm:ss} - 异常";
                });
            }
            catch { }
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
                Log("INFO", $"API响应长度: {content.Length}字节");
                var data = JToken.Parse(content);

                // 提取并保存 columns 信息（用于表格格式解析）
                // API可能直接返回 {columns, data} 或 {data: {columns, data}}
                JArray? columns = null;

                // 情况1: API直接返回 {columns: [...], data: [...]}
                if (data is JObject rootObj && rootObj["columns"] is JArray directColumns)
                {
                    columns = directColumns;
                }
                // 情况2: API返回 {data: {columns: [...], data: [...]}}
                else if (data["data"] is JObject dataObj && dataObj["columns"] is JArray nestedColumns)
                {
                    columns = nestedColumns;
                }

                if (columns != null)
                {
                    _tableColumns = columns.Select(c => c.ToString()).ToList();
                    // 持久化保存 columns
                    try
                    {
                        File.WriteAllText(_columnsFilePath, System.Text.Json.JsonSerializer.Serialize(_tableColumns));
                    }
                    catch { }
                }
                else if (File.Exists(_columnsFilePath))
                {
                    // 如果API数据没有columns，尝试从文件加载
                    try
                    {
                        var colsJson = File.ReadAllText(_columnsFilePath);
                        _tableColumns = System.Text.Json.JsonSerializer.Deserialize<List<string>>(colsJson) ?? new List<string>();
                        Log("INFO", $"从文件加载Columns: 共{_tableColumns.Count}");
                    }
                    catch { }
                }

                return data;
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

        // 使用共享方法获取过滤后的品种数据（应用同向持仓规则）
        var currentFiltered = GetFilteredStrategyProducts(data, enabledStrategies);

        // 按出现频率排序（频率高的在前）
        var productFrequency = new Dictionary<string, int>();
        foreach (var kvp in currentFiltered)
        {
            foreach (var (productId, _, _) in kvp.Value)
            {
                if (productFrequency.ContainsKey(productId))
                    productFrequency[productId]++;
                else
                    productFrequency[productId] = 1;
            }
        }
        var sortedProducts = productFrequency.OrderByDescending(x => x.Value).Select(x => x.Key).ToList();

        if (sortedProducts.Count == 0) return null;

        // 构建推送内容
        var isFirstPush = _lastPushData == null;
        var sb = new StringBuilder();

        sb.AppendLine($"📊 **盘中止损监测**");
        sb.AppendLine($"🕐 {DateTime.Now:yyyy-MM-dd HH:mm}");

        if (isFirstPush)
        {
            // 首次推送，完整内容（按频率排序）
            sb.AppendLine();
            sb.AppendLine("**📈 满足条件的品种:**");
            // 按频率排序输出
            foreach (var productId in sortedProducts)
            {
                var productInfos = currentFiltered
                    .Where(kvp => kvp.Value.Any(p => p.productId == productId))
                    .Select(kvp => {
                        var p = kvp.Value.First(x => x.productId == productId);
                        return $"{kvp.Key}({p.direction})";
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
            // 后续推送，差异对比（使用过滤后的数据进行对比）
            var previousFiltered = _lastPushData != null ? GetFilteredStrategyProducts(_lastPushData, enabledStrategies) : null;

            sb.AppendLine();
            sb.AppendLine("**🔄 变化品种:**");

            foreach (var strategy in enabledStrategies)
            {
                var currentList = currentFiltered.TryGetValue(strategy, out var cl) ? cl : new List<(string productId, string direction, double remainingRisk)>();
                var previousList = previousFiltered?.GetValueOrDefault(strategy) ?? new List<(string productId, string direction, double remainingRisk)>();

                var added = currentList.Where(cp => !previousList.Any(pp => pp.productId == cp.productId)).ToList();
                var removed = previousList.Where(pp => !currentList.Any(cp => cp.productId == pp.productId)).ToList();

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
        try
        {
            var sb = new StringBuilder();
            var dataToken = data["data"];

            // 检查是否是表格格式
            if (dataToken is JObject dataObj && dataObj["columns"] != null)
            {
                // 表格格式：使用 GetFilteredStrategyProducts
                var filtered = GetFilteredStrategyProducts(data, null);
                foreach (var kvp in filtered)
                {
                    foreach (var (productId, direction, _) in kvp.Value)
                    {
                        sb.Append($"{productId}|{kvp.Key}|0|{direction};");
                    }
                }
                return sb.ToString();
            }

            // 标准格式或直接数组
            var dataArray = dataToken as JArray;
            if (dataArray == null || dataArray.Count == 0) return "";

            // 检查是否是二维数组（表格格式但以不同方式表示）
            if (dataArray[0] is JArray)
            {
                var filtered = GetFilteredStrategyProducts(data, null);
                foreach (var kvp in filtered)
                {
                    foreach (var (productId, direction, _) in kvp.Value)
                    {
                        sb.Append($"{productId}|{kvp.Key}|0|{direction};");
                    }
                }
                return sb.ToString();
            }

            // 标准格式：[{productId:..., items:{...}}, ...]
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
                        if (rate >= 0 && direction != "None" && remainingRisk >= 0)
                        {
                            sb.Append($"{productId}|{strategy}|{rate:F6}|{direction};");
                        }
                    }
                }
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Log("ERROR", $"ExtractSignalSummary异常: {ex.Message}");
            return "";
        }
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

    private void LogToDebugFile(string content)
    {
        try
        {
            File.AppendAllText(_debugFilePath, $"[{DateTime.Now:HH:mm:ss.fff}]{Environment.NewLine}{content}{Environment.NewLine}{new string('=', 80)}{Environment.NewLine}");
        }
        catch { }
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
    // JToken 版本：自动提取 data["data"] 部分
    private Dictionary<string, List<(string productId, string direction, double remainingRisk)>> GetFilteredStrategyProducts(JToken rootData, List<string>? enabledStrategies = null)
    {
        try
        {
            // 提取 data["data"] 部分
            var dataToken = rootData["data"];
            JArray? dataArray = null;

            if (dataToken is JObject dataObj)
            {
                if (dataObj["columns"] != null && dataObj["data"] is JArray nestedData)
                {
                    // 情况2: API返回 {data: {columns: [...], data: [...]}}
                    dataArray = nestedData;
                    if (dataObj["columns"] is JArray columns)
                    {
                        _tableColumns = columns.Select(c => c.ToString()).ToList();
                        try { File.WriteAllText(_columnsFilePath, System.Text.Json.JsonSerializer.Serialize(_tableColumns)); } catch { }
                    }
                }
                else if (dataObj["data"] is JArray arr)
                {
                    // 情况3: {data: [[...], ...]}
                    dataArray = arr;
                }
            }
            else if (dataToken is JArray arr)
            {
                // 情况4: data 本身就是数组（标准格式）
                dataArray = arr;
            }
            else if (rootData is JObject rootObj && rootObj["columns"] != null && rootObj["data"] is JArray rootDataArr)
            {
                // 情况1: API直接返回 {columns: [...], data: [...]}
                dataArray = rootDataArr;
                if (rootObj["columns"] is JArray columns)
                {
                    _tableColumns = columns.Select(c => c.ToString()).ToList();
                    try { File.WriteAllText(_columnsFilePath, System.Text.Json.JsonSerializer.Serialize(_tableColumns)); } catch { }
                }
            }

            if (dataArray == null)
            {
                return new Dictionary<string, List<(string productId, string direction, double remainingRisk)>>();
            }

            return GetFilteredStrategyProducts(dataArray, enabledStrategies);
        }
        catch
        {
            return new Dictionary<string, List<(string productId, string direction, double remainingRisk)>>();
        }
    }

    // JArray 版本
    private Dictionary<string, List<(string productId, string direction, double remainingRisk)>> GetFilteredStrategyProducts(JArray dataArray, List<string>? enabledStrategies = null)
    {
        // 检查是否是需要转换的表格格式 {columns: [...], data: [[...], [...], ...]}
        // 表格格式的特征：第一个元素是数组
        if (dataArray.Count == 0 || (dataArray[0] is JArray && dataArray[0]?.Count() > 0))
        {
            return GetFilteredStrategyProductsFromTableFormat(dataArray, enabledStrategies);
        }

        // 标准格式：[{productId:..., items:{...}}, ...]
        var allStrategies = new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" };
        var strategiesToUse = enabledStrategies?.Count > 0 ? enabledStrategies : allStrategies.ToList();
        var enabledSet = new HashSet<string>(strategiesToUse);

        var result = new Dictionary<string, List<(string productId, string direction, double remainingRisk)>>();
        foreach (var strategy in strategiesToUse)
        {
            result[strategy] = new List<(string, string, double)>();
        }

        foreach (var productData in dataArray)
        {
            var productId = productData["productId"]?.ToString() ?? "";
            var items = productData["items"] as JObject;
            if (items == null) continue;

            // 调试日志：追踪品种 I 和 JD 的筛选过程
            if (productId == "I" || productId == "JD")
            {
                System.Diagnostics.Debug.WriteLine($"[DEBUG] 品种 {productId}: 开始筛选");
            }

            // 收集满足基本筛选条件的策略方向（用于当前策略筛选）
            var productDirections = new Dictionary<string, string>();
            // 收集已勾选策略的持仓方向（不限条件，仅判断direction是否相同）
            var enabledPositions = new Dictionary<string, string>();
            foreach (var strategyName in strategiesToUse)
            {
                var strategyData = items[strategyName] as JObject;
                if (strategyData != null)
                {
                    var rate = strategyData["totalRealTimeStopPriceDiffRate"]?.Value<double>() ?? 0;
                    var direction = strategyData["direction"]?.ToString() ?? "";
                    var remainingRisk = strategyData["remainingRisk"]?.Value<double>() ?? 0;
                    if (productId == "I" || productId == "JD")
                    {
                        System.Diagnostics.Debug.WriteLine($"[DEBUG] 品种 {productId} - {strategyName}: rate={rate:F6}, direction={direction}, remainingRisk={remainingRisk:F4}");
                    }
                    // 基本筛选条件：rate >= 0, direction不是None, remainingRisk >= 0
                    if (rate >= 0 && direction != "None" && remainingRisk >= 0)
                    {
                        productDirections[strategyName] = direction;
                    }
                    // 已勾选策略的持仓方向（不限条件）
                    if (direction != "None" && !string.IsNullOrEmpty(direction))
                    {
                        enabledPositions[strategyName] = direction;
                    }
                }
            }

            if (productId == "I" || productId == "JD")
            {
                System.Diagnostics.Debug.WriteLine($"[DEBUG] 品种 {productId} - enabledPositions: {string.Join(",", enabledPositions.Select(kv => $"{kv.Key}={kv.Value}"))}");
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
                        // 判断已勾选的更高策略是否同向持仓（只检查已勾选的更高策略）
                        var higherStrategies = GetHigherStrategies(strategy, allStrategies, enabledSet);
                        if (productId == "I" || productId == "JD")
                        {
                            System.Diagnostics.Debug.WriteLine($"[DEBUG] 品种 {productId} - 检查策略 {strategy}, higherStrategies={string.Join(",", higherStrategies)}");
                        }
                        if (higherStrategies.Length > 0)
                        {
                            var allHigherMatch = higherStrategies.All(hs =>
                                enabledPositions.TryGetValue(hs, out var higherDir) && higherDir == direction);
                            if (!allHigherMatch)
                            {
                                if (productId == "I" || productId == "JD")
                                {
                                    System.Diagnostics.Debug.WriteLine($"[DEBUG] 品种 {productId} - 策略 {strategy} 因更高策略方向不匹配而跳过");
                                }
                                continue;
                            }
                        }
                        result[strategy].Add((productId, direction, remainingRisk));
                        if (productId == "I" || productId == "JD")
                        {
                            System.Diagnostics.Debug.WriteLine($"[DEBUG] 品种 {productId} - 策略 {strategy} 通过筛选，加入结果");
                        }
                    }
                }
            }

        // 调试日志：输出最终结果
        System.Diagnostics.Debug.WriteLine($"[DEBUG] 筛选完成，GD15品种数: {result.GetValueOrDefault("GD15")?.Count ?? 0}");
        if (result.GetValueOrDefault("GD15")?.Any(p => p.productId == "I") == true)
        {
            System.Diagnostics.Debug.WriteLine($"[DEBUG] 品种 I 在 GD15 结果中!");
        }
        if (result.GetValueOrDefault("GD15")?.Any(p => p.productId == "JD") == true)
        {
            System.Diagnostics.Debug.WriteLine($"[DEBUG] 品种 JD 在 GD15 结果中!");
        }

        return result;
    }

    // 处理表格格式数据：{columns: [...], data: [[...], [...], ...]}
    private Dictionary<string, List<(string productId, string direction, double remainingRisk)>> GetFilteredStrategyProductsFromTableFormat(JArray dataArray, List<string>? enabledStrategies = null)
    {
        var debugLog = new StringBuilder();
        debugLog.AppendLine($"[DEBUG] GetFilteredStrategyProductsFromTableFormat 开始执行");
        debugLog.AppendLine($"[DEBUG] dataArray.Count = {dataArray.Count}");

        // 获取 columns（列名）
        var columns = _tableColumns ?? new List<string>();
        debugLog.AppendLine($"[DEBUG] columns.Count = {columns.Count}");
        if (columns.Count == 0)
        {
            debugLog.AppendLine("[DEBUG] columns为空，返回空结果");
            LogToDebugFile(debugLog.ToString());
            return new Dictionary<string, List<(string productId, string direction, double remainingRisk)>>();
        }

        var allStrategies = new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" };
        var strategiesToUse = enabledStrategies?.Count > 0 ? enabledStrategies : allStrategies.ToList();
        var enabledSet = new HashSet<string>(strategiesToUse);
        debugLog.AppendLine($"[DEBUG] strategiesToUse = {string.Join(", ", strategiesToUse)}");

        var result = new Dictionary<string, List<(string productId, string direction, double remainingRisk)>>();
        foreach (var strategy in strategiesToUse)
        {
            result[strategy] = new List<(string, string, double)>();
        }

        // 找出各列的索引（优先使用 totalRealTimeStopPriceDiffRate）
        var productIdIdx = columns.IndexOf("productId");
        var directionIdx = columns.IndexOf("direction");
        var rateIdx = columns.IndexOf("totalRealTimeStopPriceDiffRate");
        var remainingRiskIdx = columns.IndexOf("remainingRisk");

        // 检查数据是否为有效的二维数组（每行应该是数组而不是嵌套数组）
        if (dataArray.Count == 0)
        {
            return result;
        }

        var firstRow = dataArray[0];
        if (!(firstRow is JArray))
        {
            return result;
        }

        // 按productId分组数据: productId -> strategyName -> (direction, rate, remainingRisk)
        var productGroups = new Dictionary<string, Dictionary<string, (string direction, double rate, double remainingRisk)>>();

        foreach (var row in dataArray.Cast<JArray>())
        {
            // 跳过嵌套的数组（处理异常的数据结构）
            if (row.Count > 0 && row[0] is JArray)
            {
                continue;
            }

            var productId = productIdIdx >= 0 && productIdIdx < row.Count ? row[productIdIdx]?.ToString() ?? "" : "";
            if (string.IsNullOrEmpty(productId)) continue;

            var strategyName = columns.Contains("strategyName") ? row[columns.IndexOf("strategyName")]?.ToString() ?? "" : "";
            debugLog.AppendLine($"[DEBUG] 读取行: productId={productId}, strategyName={strategyName}");
            if (!strategiesToUse.Contains(strategyName)) continue;

            var rate = rateIdx >= 0 ? (row[rateIdx]?.Value<double>() ?? 0) : 0;
            var remainingRisk = remainingRiskIdx >= 0 ? (row[remainingRiskIdx]?.Value<double>() ?? 0) : 0;
            var direction = directionIdx >= 0 ? row[directionIdx]?.ToString() ?? "" : "";

            if (!productGroups.ContainsKey(productId))
            {
                productGroups[productId] = new Dictionary<string, (string, double, double)>();
            }

            productGroups[productId][strategyName] = (direction, rate, remainingRisk);
        }

        debugLog.AppendLine($"[DEBUG] productGroups 统计: 共 {productGroups.Count} 个品种");
        foreach (var pg in productGroups)
        {
            debugLog.AppendLine($"[DEBUG]   {pg.Key}: {string.Join(", ", pg.Value.Select(v => v.Key))}");
        }

        // 收集已勾选策略的持仓方向（不限条件）
        var enabledPositions = new Dictionary<string, Dictionary<string, string>>();
        foreach (var kvp in productGroups)
        {
            enabledPositions[kvp.Key] = new Dictionary<string, string>();
            foreach (var stratKvp in kvp.Value)
            {
                if (stratKvp.Value.direction != "None" && !string.IsNullOrEmpty(stratKvp.Value.direction))
                {
                    enabledPositions[kvp.Key][stratKvp.Key] = stratKvp.Value.direction;
                }
            }
        }

        // 应用筛选逻辑
        // 第一步：收集每个品种在各策略中的有效数据（用于后续排序）
        var productStrategyData = new Dictionary<string, Dictionary<string, (string direction, double rate, double remainingRisk)>>();
        foreach (var productKvp in productGroups)
        {
            var productId = productKvp.Key;
            var strategies = productKvp.Value;

            // 收集满足基本筛选条件的策略方向
            var productDirections = new Dictionary<string, string>();
            foreach (var stratKvp in strategies)
            {
                var (direction, rate, remainingRisk) = stratKvp.Value;
                if (rate >= 0 && direction != "None" && remainingRisk >= 0)
                {
                    productDirections[stratKvp.Key] = direction;
                }
            }

            foreach (var strategy in strategiesToUse)
            {
                if (!strategies.TryGetValue(strategy, out var stratData)) continue;

                var (direction, rate, remainingRisk) = stratData;
                if (rate < 0 || direction == "None" || remainingRisk < 0) continue;

                // 判断已勾选的更高策略是否同向持仓
                var higherStrategies = GetHigherStrategies(strategy, allStrategies, enabledSet);
                if (higherStrategies.Length > 0)
                {
                    var allHigherMatch = higherStrategies.All(hs =>
                        enabledPositions.TryGetValue(productId, out var positions) &&
                        positions.TryGetValue(hs, out var higherDir) &&
                        higherDir == direction);
                    if (!allHigherMatch) continue;
                }

                // 记录有效数据
                if (!productStrategyData.ContainsKey(productId))
                    productStrategyData[productId] = new Dictionary<string, (string, double, double)>();
                productStrategyData[productId][strategy] = (direction, rate, remainingRisk);
            }
        }

        // 第二步：统计每个品种被多少个策略选中（用于共振排序）
        var productStrategyCount = productStrategyData
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count);

        // 第三步：分离共振品种和单策略品种
        // 共振品种：按共振数量降序排序，相同时按字母排序
        var resonanceProducts = productStrategyCount
            .Where(kv => kv.Value >= 2)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Select(kv => kv.Key)
            .ToList();

        // 单策略品种：按字母排序
        var singleProducts = productStrategyCount
            .Where(kv => kv.Value == 1)
            .OrderBy(kv => kv.Key)
            .ToList();

        // 第四步：构建品种列表
        // 共振品种：按统一顺序排列，占据前面行（如果某策略没有该品种，该行留空）
        // 单策略品种：追加到各自策略的共振品种之后
        debugLog.AppendLine($"[DEBUG] 共振品种: {string.Join(", ", resonanceProducts)}");
        debugLog.AppendLine($"[DEBUG] 单策略品种: {string.Join(", ", singleProducts.Select(kv => $"{kv.Key}({kv.Value})"))}");

        foreach (var strategy in strategiesToUse)
        {
            result[strategy] = new List<(string, string, double)>();

            // 4.1 添加共振品种（按统一顺序）
            foreach (var productId in resonanceProducts)
            {
                if (productStrategyData.TryGetValue(productId, out var stratData) &&
                    stratData.TryGetValue(strategy, out var data))
                {
                    result[strategy].Add((productId, data.direction, data.remainingRisk));
                }
                // 如果该策略没有这个品种，该行留空（不添加）
            }

            // 4.2 添加单策略品种（追加到共振品种之后）
            foreach (var (productId, _) in singleProducts)
            {
                if (productStrategyData.TryGetValue(productId, out var stratData) &&
                    stratData.TryGetValue(strategy, out var data))
                {
                    result[strategy].Add((productId, data.direction, data.remainingRisk));
                }
            }

            debugLog.AppendLine($"[DEBUG] {strategy}: 共振{resonanceProducts.Count}个 + 单策略{result[strategy].Count - resonanceProducts.Count}个");
            debugLog.AppendLine($"[DEBUG]   品种列表: {string.Join(", ", result[strategy].Select(p => p.productId))}");
        }

        LogToDebugFile(debugLog.ToString());
        return result;
    }

    static string[] GetHigherStrategies(string strategy, string[] allStrats, HashSet<string>? enabledSet = null)
    {
        var result = strategy switch
        {
            "GD15" => new[] { "GD20", "GD25", "GD30", "GD35", "GD40" },
            "GD20" => new[] { "GD25", "GD30", "GD35", "GD40" },
            "GD25" => new[] { "GD30", "GD35", "GD40" },
            "GD30" => new[] { "GD35", "GD40" },
            "GD35" => new[] { "GD40" },
            "GD40" => Array.Empty<string>(),
            _ => Array.Empty<string>()
        };
        // 如果提供了enabledSet，则只返回已勾选的更高策略
        if (enabledSet != null)
        {
            result = result.Where(s => enabledSet.Contains(s)).ToArray();
        }
        return result;
    }

    // 根据过滤后的数据构建变化描述
    private string BuildChangeDescriptionFromFiltered(
        Dictionary<string, List<(string productId, string direction, double remainingRisk)>> currentFiltered,
        Dictionary<string, List<(string productId, string direction, double remainingRisk)>>? previousFiltered,
        out List<(string strategy, string productId, bool isAdded)> changedProducts)
    {
        changedProducts = new List<(string, string, bool)>();
        var sb = new StringBuilder();
        var allStrategies = new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" };

        foreach (var strategy in allStrategies)
        {
            if (!currentFiltered.TryGetValue(strategy, out var currentList))
                currentList = new List<(string, string, double)>();
            var previousList = previousFiltered?.GetValueOrDefault(strategy) ?? new List<(string, string, double)>();

            var added = currentList.Where(cp => !previousList.Any(pp => pp.productId == cp.productId)).ToList();
            var removed = previousList.Where(pp => !currentList.Any(cp => cp.productId == pp.productId)).ToList();

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
        Dictionary<string, List<(string productId, string direction, double remainingRisk)>> currentFiltered,
        Dictionary<string, List<(string productId, string direction, double remainingRisk)>>? previousFiltered)
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
                        Log("DEBUG", $"[防闪烁] 品种 {change.productId} 在{FlickerCacheMinutes - (int)(now - lastChangeTime).TotalMinutes}分钟内状态变化，跳过");
                        shouldInclude = false;
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
        Dictionary<string, List<(string productId, string direction, double remainingRisk)>> currentFiltered,
        Dictionary<string, List<(string productId, string direction, double remainingRisk)>>? previousFiltered)
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
                    if (currentFiltered.TryGetValue(strategy, out var list))
                    {
                        var item = list.FirstOrDefault(p => p.productId == a.productId);
                        return item.productId != null ? $"{item.productId}({item.direction})" : a.productId;
                    }
                    return a.productId;
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
        var allStrategies = new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" };
        
        // 获取已勾选的策略
        var enabledStrategies = GetEnabledStrategies();
        System.Diagnostics.Debug.WriteLine($"[DEBUG] BuildChangeDescription - enabledStrategies: {string.Join(",", enabledStrategies)}");

        // 使用共享方法获取过滤后的数据
        var currentFiltered = GetFilteredStrategyProducts(currentData);
        var previousFiltered = previousData != null ? GetFilteredStrategyProducts(previousData) : null;
        
        // 调试：输出 GD15 的品种列表
        if (currentFiltered.TryGetValue("GD15", out var gd15List))
        {
            System.Diagnostics.Debug.WriteLine($"[DEBUG] BuildChangeDescription - GD15品种: {string.Join(",", gd15List.Select(p => p.productId))}");
        }
        
        // 调试：检查 JD 是否在数据中
        System.Diagnostics.Debug.WriteLine($"[DEBUG] BuildChangeDescription - JD 在 GD15 中: {currentFiltered.GetValueOrDefault("GD15")?.Any(p => p.productId == "JD")}");

        foreach (var strategy in allStrategies)
        {
            if (!currentFiltered.TryGetValue(strategy, out var currentList))
                currentList = new List<(string, string, double)>();
            var previousList = previousFiltered?.GetValueOrDefault(strategy) ?? new List<(string, string, double)>();

            var added = currentList.Where(cp => !previousList.Any(pp => pp.productId == cp.productId)).ToList();
            var removed = previousList.Where(pp => !currentList.Any(cp => cp.productId == pp.productId)).ToList();

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
            // 获取已勾选的策略列表
            var enabledStrategies = GetEnabledStrategies();
            if (enabledStrategies.Count == 0)
            {
                Log("WARN", "没有勾选任何策略，无法生成图片");
                return null;
            }

            // 使用共享方法获取过滤后的品种数据
            var strategyProducts = GetFilteredStrategyProducts(data, enabledStrategies);
            
            // 调试日志：输出每个策略的品种列表
            Log("DEBUG", $"[BuildImageData] enabledStrategies: {string.Join(",", enabledStrategies)}");
            foreach (var strategy in enabledStrategies)
            {
                var products = strategyProducts.GetValueOrDefault(strategy) ?? new List<(string, string, double)>();
                Log("DEBUG", $"[BuildImageData] {strategy} 品种: {string.Join(",", products.Select(p => p.productId))}");
            }

            // 分离共振品种（2个及以上策略同时有的）和非共振品种
            var strategies = enabledStrategies.ToArray();

            // 统计每个品种被哪些策略包含
            var productStrategies = new Dictionary<string, List<string>>();
            foreach (var strategy in strategies)
            {
                foreach (var (productId, _, _) in strategyProducts[strategy])
                {
                    if (!productStrategies.ContainsKey(productId))
                        productStrategies[productId] = new List<string>();
                    if (!productStrategies[productId].Contains(strategy))
                        productStrategies[productId].Add(strategy);
                }
            }

            // 按共振数量降序排序
            var resonanceProducts = productStrategies
                .Where(kv => kv.Value.Count >= 2)
                .OrderByDescending(kv => kv.Value.Count)
                .ThenBy(kv => kv.Key) // 共振数量相同时按字母排序
                .Select(kv => kv.Key)
                .ToList();

            // 单策略品种（只在一个策略中出现的）
            var singleProducts = productStrategies
                .Where(kv => kv.Value.Count == 1)
                .OrderBy(kv => kv.Key) // 按字母排序
                .ToDictionary(kv => kv.Key, kv => kv.Value[0]); // productId -> onlyStrategy

            // ========== 排版监测日志 ==========
            Log("DEBUG", $"[表格排版监测] 开始分析");
            Log("DEBUG", $"[表格排版监测] 共振品种({resonanceProducts.Count}个): {string.Join(", ", resonanceProducts)}");
            Log("DEBUG", $"[表格排版监测] 单策略品种({singleProducts.Count}个): {string.Join(", ", singleProducts.Select(kv => $"{kv.Key}({kv.Value})"))}");

            // ========== 新的排序逻辑 ==========
            // 1. 共振品种：按统一顺序排列，占据前面行
            // 2. 单策略品种：在共振品种之后，按各自策略的顺序排列（不占用共振行）
            var finalProductOrder = new List<string>(resonanceProducts);

            // 构建排序后的品种数据（包含 remainingRisk）
            var sortedStrategyProducts = new Dictionary<string, List<(string productId, string direction, double remainingRisk)>>();
            foreach (var strategy in strategies)
            {
                sortedStrategyProducts[strategy] = new List<(string, string, double)>();
            }

            // 第一步：添加共振品种（按统一顺序）
            var resonanceRowCount = resonanceProducts.Count;
            for (int i = 0; i < resonanceProducts.Count; i++)
            {
                var productId = resonanceProducts[i];
                foreach (var strategy in strategies)
                {
                    var originalList = strategyProducts[strategy];
                    var product = originalList.FirstOrDefault(p => p.productId == productId);
                    if (product != default)
                    {
                        sortedStrategyProducts[strategy].Add(product);
                    }
                    // 共振品种：如果该策略没有这个品种，该行留空（不添加）
                }
            }

            // 第二步：添加单策略品种（按各自策略的顺序，追加到共振品种之后）
            foreach (var strategy in strategies)
            {
                var originalList = strategyProducts[strategy];
                foreach (var (productId, direction, remainingRisk) in originalList)
                {
                    // 只添加单策略品种（不在共振品种列表中）
                    if (!resonanceProducts.Contains(productId))
                    {
                        sortedStrategyProducts[strategy].Add((productId, direction, remainingRisk));
                    }
                }
            }

            // ========== 排版监测日志 ==========
            Log("DEBUG", $"[表格排版监测] 共振行数: {resonanceRowCount}");
            Log("DEBUG", $"[表格排版监测] 统一品种顺序(共振部分): {string.Join(" -> ", resonanceProducts)}");

            // 记录每个品种在各策略中的行号
            var productRowMapping = new Dictionary<string, Dictionary<string, int>>();

            foreach (var productId in finalProductOrder)
            {
                productRowMapping[productId] = new Dictionary<string, int>();
            }

            // 输出每个品种在各策略中的行号映射
            Log("DEBUG", $"[表格排版监测] 各品种行号映射:");
            foreach (var productId in resonanceProducts)
            {
                var stratMapping = new Dictionary<string, int>();
                for (int col = 0; col < strategies.Length; col++)
                {
                    var strategy = strategies[col];
                    var list = sortedStrategyProducts[strategy];
                    var idx = list.FindIndex(p => p.productId == productId);
                    if (idx >= 0)
                    {
                        stratMapping[strategy] = idx + 1; // 1-based
                        if (!productRowMapping.ContainsKey(productId))
                            productRowMapping[productId] = new Dictionary<string, int>();
                        productRowMapping[productId][strategy] = idx;
                    }
                }
                var mappingStr = string.Join(", ", strategies.Select(s =>
                    stratMapping.TryGetValue(s, out var row) ? $"{s}@行{row}" : $"{s}@空"));
                Log("DEBUG", $"[表格排版监测]   {productId}: {mappingStr}");
            }

            // 检查排版对齐
            bool hasAlignmentIssue = false;
            foreach (var productId in resonanceProducts)
            {
                if (productRowMapping.TryGetValue(productId, out var stratMapping))
                {
                    var filledRows = stratMapping.Values.ToList();
                    if (filledRows.Count > 1)
                    {
                        var maxRow = filledRows.Max();
                        var minRow = filledRows.Min();
                        if (maxRow != minRow) // 共振品种必须同行
                        {
                            hasAlignmentIssue = true;
                            var issueStr = string.Join(", ", strategies.Select(s =>
                                stratMapping.TryGetValue(s, out var row) ? $"{s}@行{row + 1}" : $"{s}@空"));
                            Log("WARN", $"[表格排版监测] ⚠️ 共振品种排版错位: {productId} - {issueStr}");
                        }
                    }
                }
            }

            // 输出每个策略的最终品种列表
            Log("DEBUG", $"[表格排版监测] 各策略品种列表及对应行号:");
            foreach (var strategy in strategies)
            {
                var list = sortedStrategyProducts[strategy];
                var displayList = list.Select((p, idx) => $"行{idx + 1}:{p.productId}").ToList();
                Log("DEBUG", $"[表格排版监测]   {strategy}: {string.Join(", ", displayList)}");
            }

            if (!hasAlignmentIssue)
            {
                Log("DEBUG", $"[表格排版监测] ✓ 排版对齐正常");
            }
            // ========== 排版监测日志结束 ==========

            // 图片参数：策略名 + 品种列表 = 最多 8 列
            int colCount = 1 + strategies.Length; // 策略名 + 6个策略
            int maxRows = sortedStrategyProducts.Values.Max(list => list.Count);
            if (maxRows == 0) return null;

            // ========== 计算每行应该显示什么品种 ==========
            // row -> strategy -> (productId, direction, remainingRisk) or null
            var rowDisplayData = new List<Dictionary<string, (string productId, string direction, double remainingRisk)?>>();
            var totalRows = resonanceRowCount + sortedStrategyProducts.Values.Max(list =>
            {
                // 单策略品种的最大数量
                return list.Count - resonanceRowCount;
            });

            // 共振品种行：每个品种对应一行
            for (int i = 0; i < resonanceProducts.Count; i++)
            {
                var productId = resonanceProducts[i];
                var rowDict = new Dictionary<string, (string productId, string direction, double remainingRisk)?>();
                foreach (var strategy in strategies)
                {
                    var list = sortedStrategyProducts[strategy];
                    var item = list.FirstOrDefault(p => p.productId == productId);
                    if (item.productId != null)
                    {
                        rowDict[strategy] = item; // 包含 remainingRisk
                    }
                    else
                    {
                        rowDict[strategy] = null; // 该策略没有这个品种，留空
                    }
                }
                rowDisplayData.Add(rowDict);
            }

            // 单策略品种行：按各自策略的顺序追加
            foreach (var strategy in strategies)
            {
                var list = strategyProducts[strategy]; // 使用原始列表
                int offset = 0;
                foreach (var product in list)
                {
                    // 只添加单策略品种（不在共振品种列表中）
                    if (!resonanceProducts.Contains(product.productId))
                    {
                        // 确保这一行存在
                        while (rowDisplayData.Count <= resonanceRowCount + offset)
                        {
                            rowDisplayData.Add(new Dictionary<string, (string productId, string direction, double remainingRisk)?>());
                        }
                        rowDisplayData[resonanceRowCount + offset][strategy] = (product.productId, product.direction, product.remainingRisk);
                        offset++;
                    }
                }
            }

            // 调试日志：输出单策略品种处理
            Log("DEBUG", $"[BuildImageData] 单策略品种处理: 共振行数={resonanceRowCount}");
            foreach (var strategy in strategies)
            {
                var list = strategyProducts[strategy];
                var singleProductsInStrategy = list.Where(p => !resonanceProducts.Contains(p.productId)).ToList();
                if (singleProductsInStrategy.Any())
                {
                    Log("DEBUG", $"[BuildImageData] {strategy} 单策略品种: {string.Join(",", singleProductsInStrategy.Select(p => p.productId))}");
                }
            }
            
            // 计算实际的总行数
            totalRows = rowDisplayData.Count;
            
            // 调试日志：输出 rowDisplayData
            Log("DEBUG", $"[BuildImageData] rowDisplayData 行数: {rowDisplayData.Count}, 共振行数: {resonanceRowCount}");
            for (int i = 0; i < rowDisplayData.Count; i++)
            {
                var row = rowDisplayData[i];
                var rowContent = string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value?.productId ?? "空"}"));
                Log("DEBUG", $"[BuildImageData] 行{i}: {rowContent}");
            }

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

            // 绘制数据列 - 按行绘制
            y += titleHeight;

            // 遍历每一行
            for (int row = 0; row < totalRows; row++)
            {
                x = padding;

                // 获取当前行的品种信息
                var rowDict = rowDisplayData[row];

                // 统计每个品种在当前行出现的次数（用于高亮共振）
                var productCounts = rowDict.Values
                    .Where(v => v.HasValue)
                    .GroupBy(v => v.Value.productId)
                    .ToDictionary(g => g.Key, g => g.Count());
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
                    var strategy = strategies[col];
                    var cw = colWidths[col + 1];

                    // 判断当前行的品种是否在多个策略中出现（共振）
                    var cellData = rowDict.TryGetValue(strategy, out var cellValue) ? cellValue : null;
                    var isRepeated = false;
                    if (cellData.HasValue)
                    {
                        isRepeated = repeatedProducts.Contains(cellData.Value.productId);
                    }

                    // 重复品种使用深灰色背景
                    if (isRepeated)
                        g.FillRectangle(new SolidBrush(Color.FromArgb(60, 60, 65)), x, y, cw, cellHeight);
                    else
                        g.FillRectangle(new SolidBrush(Color.FromArgb(35, 35, 35)), x, y, cw, cellHeight);
                    g.DrawRectangle(gridPen, x, y, cw, cellHeight);

                    // 如果该列当前行有品种数据，则绘制
                    if (cellData.HasValue)
                    {
                        var (pId, direction, remainingRisk) = cellData.Value;
                        var color = direction.ToLower() == "long" ? Color.FromArgb(220, 80, 80) :
                                    direction.ToLower() == "short" ? Color.FromArgb(80, 200, 120) :
                                    Color.White;
                        using var productBrush = new SolidBrush(color);
                        
                        // 合约名（上方）
                        var pSize = g.MeasureString(pId, cellFont);
                        float drawX = x + (cw - pSize.Width) / 2;
                        float drawY = y + 2; // 上方留2像素
                        if (pSize.Width > cw - 8)
                        {
                            float scale = (cw - 8) / pSize.Width;
                            using var scaledFont = new Font(System.Drawing.FontFamily.GenericMonospace, 10 * scale);
                            var scaledSize = g.MeasureString(pId, scaledFont);
                            g.DrawString(pId, scaledFont, productBrush, x + (cw - scaledSize.Width) / 2, drawY);
                        }
                        else
                        {
                            g.DrawString(pId, cellFont, productBrush, drawX, drawY);
                        }
                        
                        // remainingRisk 百分比（下方）
                        var riskText = $"{remainingRisk * 100:F2}%";
                        using var riskFont = new Font(System.Drawing.FontFamily.GenericMonospace, 7);
                        var riskSize = g.MeasureString(riskText, riskFont);
                        float riskDrawX = x + (cw - riskSize.Width) / 2;
                        float riskDrawY = y + cellHeight - riskSize.Height - 2; // 下方留2像素
                        using var riskBrush = new SolidBrush(Color.FromArgb(180, 180, 180)); // 浅灰色
                        g.DrawString(riskText, riskFont, riskBrush, riskDrawX, riskDrawY);
                    }
                    // 如果该列当前行没有品种数据，则留空（背景已绘制）
                    x += cw;
                }
                y += cellHeight;
            }

            // 保存图片
            var imagePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"signal_{DateTime.Now:yyyyMMddHHmmss}.png");
            bitmap.Save(imagePath, ImageFormat.Png);
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
            var failReason = "";

            foreach (var terminalId in _selectedTerminalIds)
            {
                var terminal = terminals.FirstOrDefault(t => t.TerminalId == terminalId);
                if (terminal == null || string.IsNullOrEmpty(terminal.ImageApiKey) || string.IsNullOrEmpty(terminal.ImageSecretKey))
                {
                    failCount++;
                    continue;
                }

                // 获取 AccessToken
                var token = await GetTenantAccessTokenAsync(terminal.ImageApiKey, terminal.ImageSecretKey);
                if (string.IsNullOrEmpty(token))
                {
                    failReason = "获取AccessToken失败";
                    failCount++;
                    continue;
                }

                // 上传图片获取 image_key
                var imageKey = await UploadImageDataAsync(imagePath, token);
                if (string.IsNullOrEmpty(imageKey))
                {
                    failReason = "上传图片失败";
                    failCount++;
                    continue;
                }

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
                }
                else
                {
                    failReason = $"HTTP {response.StatusCode}";
                    failCount++;
                }
            }

            if (successCount > 0)
            {
                Log("INFO", "飞书图片已发送成功");
                return true;
            }
            else
            {
                Log("ERROR", $"飞书图片发送失败: {failReason}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"飞书图片发送失败: {ex.Message}");
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
        }
        catch { }
        return null;
    }

    private async Task<bool> PushTextToFeishuAsync(string content)
    {
        try
        {
            if (_selectedTerminalIds.Count == 0) return false;

            var terminals = _databaseService.GetAllTerminalConfigs();
            var successCount = 0;
            var failReason = "";

            foreach (var terminalId in _selectedTerminalIds)
            {
                var terminal = terminals.FirstOrDefault(t => t.TerminalId == terminalId);
                if (terminal == null || string.IsNullOrEmpty(terminal.TextWebhook))
                {
                    failReason = "Webhook未配置";
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
                    failReason = $"HTTP {response.StatusCode}";
                }
            }

            if (successCount > 0)
            {
                Log("INFO", "飞书文字已发送成功");
                return true;
            }
            else
            {
                Log("ERROR", $"飞书文字发送失败: {failReason}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"飞书文字发送失败: {ex.Message}");
            return false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _uiTimer?.Stop();
        _uiTimer = null;
        _isMonitoring = false;
        base.OnClosed(e);
    }
}
