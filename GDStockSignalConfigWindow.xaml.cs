using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InfoTransfer.Models;
using InfoTransfer.Services;

namespace InfoTransfer;

public partial class GDStockSignalConfigWindow : Window
{
    private readonly DatabaseService _databaseService;
    private readonly ConfigService _configService;
    private readonly Action<string, string> _logCallback;
    private readonly ImageGeneratorService _imageGenerator;
    private GDStockSignalConfig? _currentConfig;
    private System.Timers.Timer? _monitorTimer;
    private bool _isMonitoring;
    private bool _isDisposing;
    private System.Threading.Timer? _autoSaveTimer;
    private readonly object _autoSaveLock = new();
    private List<CheckBox> _terminalCheckBoxes = new();

    // 历史记录相关
    private readonly string _historyFilePath;
    private HashSet<string> _historySet = new();
    private readonly object _historyLock = new();

    // 上次推送的品种记录，用于检测变化
    private Dictionary<string, HashSet<string>> _lastPushedProducts = new();
    private readonly object _lastPushedLock = new();
    private readonly string _lastPushedFilePath;

    // 日志文件路径
    private readonly string _logFilePath;
    private readonly object _logFileLock = new();

    public GDStockSignalConfigWindow(DatabaseService databaseService, ConfigService configService, Action<string, string> logCallback)
    {
        InitializeComponent();

        _databaseService = databaseService;
        _configService = configService;
        _logCallback = logCallback;
        _imageGenerator = new ImageGeneratorService();

        // 初始化日志文件
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDir = Path.Combine(appDataPath, "InfoTransfer", "logs");
        if (!Directory.Exists(appDir))
        {
            Directory.CreateDirectory(appDir);
        }
        _logFilePath = Path.Combine(appDir, $"GDStock_{DateTime.Now:yyyyMMdd}.log");

        // 初始化历史记录文件路径（与期货保持一致，使用 Local 目录）
        var configDir = Path.Combine(appDataPath, "InfoTransfer");
        if (!Directory.Exists(configDir))
        {
            Directory.CreateDirectory(configDir);
        }
        _historyFilePath = Path.Combine(configDir, "stock_stop_loss_history.json");
        _lastPushedFilePath = Path.Combine(configDir, "stock_last_pushed.json");
        LoadHistory();
        LoadLastPushedProducts();

        LogToFile("INFO", "窗口初始化完成");

        Loaded += GDStockSignalConfigWindow_Loaded;
    }

    private void SafeLog(string level, string message)
    {
        try
        {
            _logCallback?.Invoke(level, message);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Log Callback Error] {ex.Message}");
        }

        try
        {
            LogToFile(level, message);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Log File Error] {ex.Message}");
        }
    }

    private void LogToFile(string level, string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            lock (_logFileLock)
            {
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(_logFilePath, logEntry);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LogToFile Error] {ex.Message}");
        }
    }

    private void GDStockSignalConfigWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadOrCreateConfig();
        LoadTerminals();
        UpdateUIState();
        BindAutoSaveEvents();
    }

    private void BindAutoSaveEvents()
    {
        TxtApiBaseUrl.LostFocus += (s, e) => ScheduleAutoSave();
        TxtApiToken.LostFocus += (s, e) => ScheduleAutoSave();
        TxtDayStartTime.LostFocus += (s, e) => ScheduleAutoSave();
        TxtDayEndTime.LostFocus += (s, e) => ScheduleAutoSave();
        TxtFixedTimeMinutes.LostFocus += (s, e) => ScheduleAutoSave();
        TxtIntervalMinutes.LostFocus += (s, e) => ScheduleAutoSave();
        TxtRealTimeStopValue.LostFocus += (s, e) => ScheduleAutoSave();
        TxtRemainingRiskValue.LostFocus += (s, e) => ScheduleAutoSave();

        ChkGD15.Checked += (s, e) => ScheduleAutoSave();
        ChkGD15.Unchecked += (s, e) => ScheduleAutoSave();
        ChkGD20.Checked += (s, e) => ScheduleAutoSave();
        ChkGD20.Unchecked += (s, e) => ScheduleAutoSave();
        ChkGD25.Checked += (s, e) => ScheduleAutoSave();
        ChkGD25.Unchecked += (s, e) => ScheduleAutoSave();
        ChkGD30.Checked += (s, e) => ScheduleAutoSave();
        ChkGD30.Unchecked += (s, e) => ScheduleAutoSave();
        ChkEnableText.Checked += (s, e) => ScheduleAutoSave();
        ChkEnableText.Unchecked += (s, e) => ScheduleAutoSave();
        ChkEnableImage.Checked += (s, e) => ScheduleAutoSave();
        ChkEnableImage.Unchecked += (s, e) => ScheduleAutoSave();
        ChkRealTimeStopPriceDiffRate.Checked += (s, e) => ScheduleAutoSave();
        ChkRealTimeStopPriceDiffRate.Unchecked += (s, e) => ScheduleAutoSave();
        ChkRemainingRisk.Checked += (s, e) => ScheduleAutoSave();
        ChkRemainingRisk.Unchecked += (s, e) => ScheduleAutoSave();

        RbFixedTime.Checked += (s, e) => ScheduleAutoSave();
        RbInterval.Checked += (s, e) => ScheduleAutoSave();
    }

    #region 历史记录管理

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(_historyFilePath))
            {
                var json = File.ReadAllText(_historyFilePath);
                var items = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(json);
                if (items != null)
                {
                    _historySet = new HashSet<string>(items);
                    SafeLog("INFO", $"已加载历史记录 {items.Count} 条");
                }
            }
        }
        catch (Exception ex)
        {
            SafeLog("WARN", $"加载历史记录失败: {ex.Message}");
        }
    }

    private void SaveHistory()
    {
        try
        {
            lock (_historyLock)
            {
                var items = _historySet.ToList();
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(items, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(_historyFilePath, json);
            }
        }
        catch (Exception ex)
        {
            SafeLog("WARN", $"保存历史记录失败: {ex.Message}");
        }
    }

    private string MakeHistoryKey(string strategyName, string productId)
    {
        return $"{strategyName}|{productId}";
    }

    private bool IsInHistory(string strategyName, string productId)
    {
        lock (_historyLock)
        {
            return _historySet.Contains(MakeHistoryKey(strategyName, productId));
        }
    }

    private void AddToHistory(string strategyName, string productId)
    {
        lock (_historyLock)
        {
            _historySet.Add(MakeHistoryKey(strategyName, productId));
        }
    }

    private void RemoveFromHistory(string strategyName, string productId)
    {
        lock (_historyLock)
        {
            _historySet.Remove(MakeHistoryKey(strategyName, productId));
        }
    }

    #endregion

    #region 上次推送品种持久化（每次从文件读取）

    private Dictionary<string, HashSet<string>> LoadLastPushedProducts()
    {
        var result = new Dictionary<string, HashSet<string>>();
        try
        {
            if (File.Exists(_lastPushedFilePath))
            {
                var json = File.ReadAllText(_lastPushedFilePath);
                var dict = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
                if (dict != null)
                {
                    result = dict.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new HashSet<string>(kvp.Value)
                    );
                    SafeLog("INFO", $"加载上次推送记录成功，共 {dict.Count} 个策略");
                }
            }
            else
            {
                SafeLog("INFO", "未找到上次推送记录文件，首次启动");
            }
        }
        catch (Exception ex)
        {
            SafeLog("WARN", $"加载上次推送记录失败: {ex.Message}");
        }
        return result;
    }

    private void SaveLastPushedProducts(Dictionary<string, HashSet<string>> products)
    {
        try
        {
            var dict = products.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToList()
            );
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(dict, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(_lastPushedFilePath, json);
            SafeLog("DEBUG", $"保存上次推送记录成功");
        }
        catch (Exception ex)
        {
            SafeLog("WARN", $"保存上次推送记录失败: {ex.Message}");
        }
    }

    #endregion

    private void ScheduleAutoSave()
    {
        lock (_autoSaveLock)
        {
            _autoSaveTimer?.Dispose();
            _autoSaveTimer = new System.Threading.Timer(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    SaveConfigFromUI();
                    SaveConfigToDatabase();
                });
            }, null, 500, Timeout.Infinite);
        }
    }

    private void SaveConfigToDatabase()
    {
        if (_currentConfig == null) return;

        try
        {
            _databaseService.SaveGDStockSignalConfig(_currentConfig);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"自动保存配置失败: {ex.Message}");
        }
    }

    private void LoadTerminals()
    {
        TerminalCheckBoxes.Items.Clear();
        _terminalCheckBoxes.Clear();

        var savedTerminals = new List<string>();
        if (_currentConfig != null && !string.IsNullOrEmpty(_currentConfig.TerminalId))
        {
            savedTerminals = _currentConfig.TerminalId.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        foreach (var config in _configService.Config.FeishuPushConfig.Configs)
        {
            var checkBox = new CheckBox
            {
                Content = config.TerminalId,
                Tag = config.TerminalId,
                Margin = new Thickness(0, 0, 15, 5),
                IsChecked = savedTerminals.Contains(config.TerminalId)
            };
            checkBox.Checked += (s, e) => ScheduleAutoSave();
            checkBox.Unchecked += (s, e) => ScheduleAutoSave();
            TerminalCheckBoxes.Items.Add(checkBox);
            _terminalCheckBoxes.Add(checkBox);
        }
    }

    private void LoadOrCreateConfig()
    {
        var configs = _databaseService.GetAllGDStockSignalConfigs();
        if (configs.Count > 0)
        {
            _currentConfig = configs[0];
            LoadConfigToUI();
        }
        else
        {
            _currentConfig = new GDStockSignalConfig();
        }
    }

    private void LoadConfigToUI()
    {
        if (_currentConfig == null) return;

        TxtApiBaseUrl.Text = _currentConfig.ApiBaseUrl;
        TxtApiToken.Text = _currentConfig.ApiToken;
        TxtDayStartTime.Text = _currentConfig.MonitorStartTime;
        TxtDayEndTime.Text = _currentConfig.MonitorEndTime;
        TxtIntervalMinutes.Text = _currentConfig.MonitorIntervalMinutes.ToString();
        RbFixedTime.IsChecked = _currentConfig.UseFixedTimePoints;
        RbInterval.IsChecked = !_currentConfig.UseFixedTimePoints;
        TxtIntervalMinutes.IsEnabled = !_currentConfig.UseFixedTimePoints;
        TxtFixedTimeMinutes.IsEnabled = _currentConfig.UseFixedTimePoints;
        TxtFixedTimeMinutes.Text = string.IsNullOrEmpty(_currentConfig.FixedTimeMinutes) ? "0,15,30,45" : _currentConfig.FixedTimeMinutes;

        ChkEnableText.IsChecked = _currentConfig.EnableText;
        ChkEnableImage.IsChecked = _currentConfig.EnableImage;

        ChkGD15.IsChecked = _currentConfig.EnableGD15;
        ChkGD20.IsChecked = _currentConfig.EnableGD20;
        ChkGD25.IsChecked = _currentConfig.EnableGD25;
        ChkGD30.IsChecked = _currentConfig.EnableGD30;

        ChkRealTimeStopPriceDiffRate.IsChecked = _currentConfig.EnableRealTimeStopPriceDiffRateCondition;
        ChkRemainingRisk.IsChecked = _currentConfig.EnableRemainingRiskCondition;
        TxtRealTimeStopValue.Text = _currentConfig.RealTimeStopPriceDiffRateValue.ToString();
        TxtRemainingRiskValue.Text = _currentConfig.RemainingRiskValue.ToString();

        _isMonitoring = _currentConfig.IsEnabled;
        UpdateUIState();
    }

    private void SaveConfigFromUI()
    {
        if (_currentConfig == null)
        {
            _currentConfig = new GDStockSignalConfig();
        }

        _currentConfig.ApiBaseUrl = TxtApiBaseUrl.Text.Trim();
        _currentConfig.ApiToken = TxtApiToken.Text.Trim();
        _currentConfig.MonitorStartTime = TxtDayStartTime.Text.Trim();
        _currentConfig.MonitorEndTime = TxtDayEndTime.Text.Trim();
        _currentConfig.MonitorIntervalMinutes = int.TryParse(TxtIntervalMinutes.Text, out var interval) ? interval : 30;
        _currentConfig.UseFixedTimePoints = RbFixedTime.IsChecked == true;
        _currentConfig.FixedTimeMinutes = TxtFixedTimeMinutes.Text.Trim();

        var selectedTerminals = _terminalCheckBoxes
            .Where(cb => cb.IsChecked == true)
            .Select(cb => cb.Tag?.ToString())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
        _currentConfig.TerminalId = string.Join(",", selectedTerminals);

        _currentConfig.EnableText = ChkEnableText.IsChecked == true;
        _currentConfig.EnableImage = ChkEnableImage.IsChecked == true;
        _currentConfig.IsEnabled = _isMonitoring;

        _currentConfig.EnableGD15 = ChkGD15.IsChecked == true;
        _currentConfig.EnableGD20 = ChkGD20.IsChecked == true;
        _currentConfig.EnableGD25 = ChkGD25.IsChecked == true;
        _currentConfig.EnableGD30 = ChkGD30.IsChecked == true;
        // 股票只有 GD15-GD30
        _currentConfig.EnableGD35 = false;
        _currentConfig.EnableGD40 = false;

        _currentConfig.EnableRealTimeStopPriceDiffRateCondition = ChkRealTimeStopPriceDiffRate.IsChecked == true;
        _currentConfig.EnableRemainingRiskCondition = ChkRemainingRisk.IsChecked == true;
        _currentConfig.RealTimeStopPriceDiffRateValue = double.TryParse(TxtRealTimeStopValue.Text, out var stopVal) ? stopVal : 0;
        _currentConfig.RemainingRiskValue = double.TryParse(TxtRemainingRiskValue.Text, out var riskVal) ? riskVal : 0;

        _currentConfig.UpdatedAt = DateTime.Now;
    }

    private void UpdateUIState()
    {
        if (_isDisposing) return;

        try
        {
            Dispatcher.Invoke(() =>
            {
                StatusIndicator.Fill = _isMonitoring
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#52C41A"))
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D9D9D9"));

                TxtStatus.Text = _isMonitoring ? "运行中" : "已停止";
                BtnStartMonitor.IsEnabled = !_isMonitoring;
                BtnStopMonitor.IsEnabled = _isMonitoring;
            });
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    private void RbFixedTime_Checked(object sender, RoutedEventArgs e)
    {
        if (TxtIntervalMinutes == null) return;
        TxtIntervalMinutes.IsEnabled = false;
        TxtFixedTimeMinutes.IsEnabled = true;
        TxtFixedTimeHint.Text = "每小时的指定分钟数进行数据检查，如：0,30 表示0分和30分时检查";
    }

    private void RbInterval_Checked(object sender, RoutedEventArgs e)
    {
        if (TxtIntervalMinutes == null) return;
        TxtIntervalMinutes.IsEnabled = true;
        TxtFixedTimeMinutes.IsEnabled = false;
        TxtFixedTimeHint.Text = "每间隔指定分钟数检查一次";
    }

    private void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveConfigFromUI();
            _databaseService.SaveGDStockSignalConfig(_currentConfig!);
            SafeLog("INFO", "股票监控配置已保存");
            MessageBox.Show("配置已保存", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SafeLog("ERROR", $"保存配置失败: {ex.Message}");
            MessageBox.Show($"保存配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnTestApi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveConfigFromUI();

            if (string.IsNullOrWhiteSpace(_currentConfig?.ApiBaseUrl))
            {
                MessageBox.Show("请输入API完整地址", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Dispatcher.Invoke(() =>
            {
                BtnTestApi.IsEnabled = false;
                BtnTestApi.Content = "测试中...";
            });

            // 股票API不需要添加Strategys参数，直接使用配置的URL
            var fullUrl = _currentConfig.ApiBaseUrl.Trim();
            if (!fullUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !fullUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                fullUrl = "http://" + fullUrl;
            }

            SafeLog("INFO", $"测试API连接: {fullUrl}");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrWhiteSpace(_currentConfig.ApiToken))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _currentConfig.ApiToken);
            }

            var response = await client.GetAsync(fullUrl);
            var content = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                SafeLog("INFO", "API连接成功!");
                MessageBox.Show("API连接成功!", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                SafeLog("ERROR", $"API返回错误: {response.StatusCode}");
                MessageBox.Show($"API返回错误: {response.StatusCode}\n{content}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            SafeLog("ERROR", $"API连接失败: {ex.Message}");
            MessageBox.Show($"API连接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                BtnTestApi.IsEnabled = true;
                BtnTestApi.Content = "测试API连接";
            });
        }
    }

    private void BtnStartMonitor_Click(object sender, RoutedEventArgs e)
    {
        StartMonitor();
    }

    private void BtnStopMonitor_Click(object sender, RoutedEventArgs e)
    {
        StopMonitor();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void BtnCheckNow_Click(object sender, RoutedEventArgs e)
    {
        // 立即执行一次检查（忽略时间限制）
        SafeLog("INFO", "执行立即检查...");
        Task.Run(async () =>
        {
            await CheckConditionsOnceAsync();
        });
    }

    public void StartMonitor()
    {
        SafeLog("INFO", "准备启动股票监控...");

        SaveConfigFromUI();
        _databaseService.SaveGDStockSignalConfig(_currentConfig!);

        _isMonitoring = true;
        SafeLog("INFO", "股票监控已启动");

        UpdateUIState();
        StartMonitorTimer();
    }

    private void StopMonitor()
    {
        _isMonitoring = false;
        SafeLog("INFO", "股票监控已停止");

        UpdateUIState();
        StopMonitorTimer();
    }

    private void StartMonitorTimer()
    {
        StopMonitorTimer();

        var intervalMinutes = _currentConfig?.MonitorIntervalMinutes ?? 1;
        var intervalMs = intervalMinutes * 60 * 1000;
        if (intervalMs < 10000) intervalMs = 10000;

        SafeLog("INFO", $"启动定时器，间隔 {intervalMinutes} 分钟 ({intervalMs}ms)");
        SafeLog("INFO", $"[Timer] _isMonitoring={_isMonitoring}, _isDisposing={_isDisposing}");

        _monitorTimer = new System.Timers.Timer(intervalMs);
        _monitorTimer.AutoReset = true;
        _monitorTimer.Elapsed += async (s, e) =>
        {
            try
            {
                SafeLog("DEBUG", $"[Timer] Elapsed 触发，_isMonitoring={_isMonitoring}, _isDisposing={_isDisposing}");
                
                if (_isMonitoring && !_isDisposing)
                {
                    SafeLog("DEBUG", "[Timer] 开始执行 CheckConditionsAsync");
                    await CheckConditionsAsync();
                }
                else
                {
                    SafeLog("DEBUG", "[Timer] 跳过（监控未启动或正在关闭）");
                }
            }
            catch (Exception ex)
            {
                SafeLog("ERROR", $"[Timer] 定时任务异常: {ex.Message}");
            }
        };
        _monitorTimer.Start();

        SafeLog("INFO", $"监控定时器已启动，AutoReset=true");

        // 立即执行一次检查
        SafeLog("INFO", "[Timer] 立即执行一次检查...");
        _ = CheckConditionsAsync();
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

    private async Task CheckConditionsAsync()
    {
        SafeLog("INFO", $"[CheckConditionsAsync] 开始执行，_isMonitoring={_isMonitoring}, _currentConfig={(_currentConfig != null)}");
        
        if (_currentConfig == null || !_isMonitoring || _isDisposing)
        {
            SafeLog("INFO", $"[CheckConditionsAsync] 跳过，_currentConfig={(_currentConfig != null)}, _isMonitoring={_isMonitoring}, _isDisposing={_isDisposing}");
            return;
        }

        try
        {
            SafeLog("INFO", $"[{DateTime.Now:HH:mm:ss}] ========== 开始检查股票GD策略信号 ==========");

            if (!IsInMonitorPeriod())
            {
                SafeLog("INFO", $"[{DateTime.Now:HH:mm:ss}] 不在监控时间段内");
                return;
            }

            var data = await FetchSignalDataAsync();
            if (data == null)
            {
                SafeLog("ERROR", "获取信号数据失败");
                return;
            }

            SafeLog("INFO", $"获取到API数据，开始检查条件...");
            
            // 获取当前满足条件的品种（按策略分组）
            var currentProducts = GetCurrentProductsByStrategy(data);
            SafeLog("INFO", $"当前满足条件的品种: {string.Join(", ", currentProducts.Select(kv => $"{kv.Key}:{string.Join(",", kv.Value)}"))}");

            // 每次从文件读取上次推送记录
            var lastPushedProducts = LoadLastPushedProducts();

            // 计算变化
            var (added, removed, isFirstRun) = CalculateChanges(currentProducts, lastPushedProducts);
            
            bool hasChanges = added.Count > 0 || removed.Count > 0;
            SafeLog("INFO", $"变化检测结果: 新增={added.Count}个, 减少={removed.Count}个");

            // 首次运行且无变化时，也推送一次
            if (!hasChanges && !isFirstRun)
            {
                SafeLog("INFO", $"[{DateTime.Now:HH:mm:ss}] 品种列表无变化，不推送");
                return;
            }

            // 保存当前记录
            SaveLastPushedProducts(currentProducts);

            // 获取名称映射
            var idToNameMap = GetIdToNameMap(data);

            // 构建变化消息
            var textContent = BuildChangeMessage(added, removed, idToNameMap);
            var rawDataJson = data.ToString(Newtonsoft.Json.Formatting.None);
            
            await PushNotificationAsync(textContent, rawDataJson);
            SafeLog("INFO", $"推送完成");
        }
        catch (Exception ex)
        {
            SafeLog("ERROR", $"检查监控条件失败: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// 获取当前满足条件的品种（按策略分组）
    /// </summary>
    private Dictionary<string, HashSet<string>> GetCurrentProductsByStrategy(Newtonsoft.Json.Linq.JToken data)
    {
        var result = new Dictionary<string, HashSet<string>>();
        
        if (_currentConfig == null) return result;

        var enabledStrategies = GetEnabledStrategies();
        foreach (var strategy in enabledStrategies)
        {
            result[strategy] = new HashSet<string>();
        }

        var dataArray = data["data"] as Newtonsoft.Json.Linq.JArray;
        if (dataArray == null) return result;

        bool checkRealTimeStop = _currentConfig.EnableRealTimeStopPriceDiffRateCondition;
        bool checkRemainingRisk = _currentConfig.EnableRemainingRiskCondition;
        double realTimeStopThreshold = _currentConfig.RealTimeStopPriceDiffRateValue / 100.0;
        double remainingRiskThreshold = _currentConfig.RemainingRiskValue / 100.0;

        foreach (var productData in dataArray)
        {
            var productId = productData["productId"]?.ToString();
            if (string.IsNullOrEmpty(productId)) continue;

            var items = productData["items"] as Newtonsoft.Json.Linq.JObject;
            if (items == null) continue;

            foreach (var strategy in enabledStrategies)
            {
                var strategyObj = items[strategy] as Newtonsoft.Json.Linq.JObject;
                if (strategyObj == null) continue;

                var direction = (int?)strategyObj["direction"] ?? -1;
                if (direction != 1) continue;

                var realTimeStop = (double?)(strategyObj["realTimeStopPriceDiffRate"]) ?? 0;
                var remainingRisk = (double?)(strategyObj["remainingRisk"]) ?? 0;

                bool match = true;
                if (checkRealTimeStop && realTimeStop <= realTimeStopThreshold) match = false;
                if (checkRemainingRisk && remainingRisk <= remainingRiskThreshold) match = false;

                SafeLog("DEBUG", $"[{productId}] {strategy}: direction={direction}, realTimeStop={realTimeStop}, risk={remainingRisk}, match={match}");

                if (match)
                {
                    result[strategy].Add(productId);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 获取品种ID到名称的映射
    /// </summary>
    private Dictionary<string, string> GetIdToNameMap(Newtonsoft.Json.Linq.JToken data)
    {
        var map = new Dictionary<string, string>();
        var dataArray = data["data"] as Newtonsoft.Json.Linq.JArray;
        if (dataArray == null) return map;

        foreach (var productData in dataArray)
        {
            var productId = productData["productId"]?.ToString();
            if (string.IsNullOrEmpty(productId)) continue;
            var name = productData["name"]?.ToString() ?? productId;
            map[productId] = name;
        }
        return map;
    }

    /// <summary>
    /// 计算品种变化（新增和减少）
    /// </summary>
    private (Dictionary<string, List<string>> added, Dictionary<string, List<string>> removed, bool isFirstRun) CalculateChanges(Dictionary<string, HashSet<string>> currentProducts, Dictionary<string, HashSet<string>> lastPushedProducts)
    {
        var added = new Dictionary<string, List<string>>();
        var removed = new Dictionary<string, List<string>>();

        if (lastPushedProducts.Count == 0)
        {
            foreach (var kv in currentProducts)
            {
                if (kv.Value.Count > 0)
                {
                    added[kv.Key] = kv.Value.ToList();
                }
            }
            return (added, removed, true); // 首次运行
        }

        foreach (var strategy in currentProducts.Keys)
        {
            var current = currentProducts[strategy];
            var previous = lastPushedProducts.TryGetValue(strategy, out var p) ? p : new HashSet<string>();

            var newOnes = current.Except(previous).ToList();
            var goneOnes = previous.Except(current).ToList();

            if (newOnes.Count > 0) added[strategy] = newOnes;
            if (goneOnes.Count > 0) removed[strategy] = goneOnes;
        }

        return (added, removed, false);
    }

    /// <summary>
    /// 构建变化消息文本
    /// </summary>
    private string BuildChangeMessage(Dictionary<string, List<string>> added, Dictionary<string, List<string>> removed, Dictionary<string, string> idToNameMap)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("【股票 GD 信号】");
        sb.AppendLine($"时间: {DateTime.Now:HH:mm}");

        if (added.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("本次增加：");
            foreach (var kv in added.OrderBy(k => k.Key))
            {
                if (kv.Value.Count > 0)
                {
                    var names = kv.Value.Select(id => idToNameMap.GetValueOrDefault(id, id)).OrderBy(n => n);
                    sb.AppendLine($"  {kv.Key}-{string.Join("、", names)}");
                }
            }
        }

        if (removed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("本次减少：");
            foreach (var kv in removed.OrderBy(k => k.Key))
            {
                if (kv.Value.Count > 0)
                {
                    var names = kv.Value.Select(id => idToNameMap.GetValueOrDefault(id, id)).OrderBy(n => n);
                    sb.AppendLine($"  {kv.Key}-{string.Join("、", names)}");
                }
            }
        }

        return sb.ToString();
    }

    private async Task CheckConditionsOnceAsync()
    {
        if (_currentConfig == null) return;

        try
        {
            SafeLog("INFO", $"[{DateTime.Now:HH:mm:ss}] 执行立即检查...");

            var data = await FetchSignalDataAsync();
            if (data == null)
            {
                SafeLog("ERROR", "获取信号数据失败");
                return;
            }

            // 获取当前满足条件的品种（按策略分组）
            var currentProducts = GetCurrentProductsByStrategy(data);
            SafeLog("INFO", $"当前满足条件的品种: {string.Join(", ", currentProducts.Select(kv => $"{kv.Key}:{string.Join(",", kv.Value)}"))}");

            // 每次从文件读取上次推送记录
            var lastPushedProducts = LoadLastPushedProducts();

            // 计算变化
            var (added, removed, isFirstRun) = CalculateChanges(currentProducts, lastPushedProducts);
            
            bool hasChanges = added.Count > 0 || removed.Count > 0;
            SafeLog("INFO", $"变化检测结果: 新增={added.Count}个, 减少={removed.Count}个");

            // 首次运行且无变化时，也推送一次
            if (!hasChanges && !isFirstRun)
            {
                SafeLog("INFO", $"[{DateTime.Now:HH:mm:ss}] 品种列表无变化，不推送");
                return;
            }

            // 保存当前记录
            SaveLastPushedProducts(currentProducts);

            // 获取名称映射
            var idToNameMap = GetIdToNameMap(data);

            // 构建变化消息
            var textContent = BuildChangeMessage(added, removed, idToNameMap);
            var rawDataJson = data.ToString(Newtonsoft.Json.Formatting.None);
            
            await PushNotificationAsync(textContent, rawDataJson);
            SafeLog("INFO", $"推送完成");
        }
        catch (Exception ex)
        {
            SafeLog("ERROR", $"检查失败: {ex.Message}");
        }
    }

    private bool IsInMonitorPeriod()
    {
        if (_currentConfig == null)
        {
            SafeLog("DEBUG", "[IsInMonitorPeriod] _currentConfig 为空");
            return false;
        }

        var now = DateTime.Now;
        var dayOfWeek = now.DayOfWeek;
        SafeLog("DEBUG", $"[IsInMonitorPeriod] 当前: {now:HH:mm:ss}, 星期={dayOfWeek}, UseFixedTimePoints={_currentConfig.UseFixedTimePoints}, Interval={_currentConfig.MonitorIntervalMinutes}");

        if (dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday)
        {
            SafeLog("DEBUG", "[IsInMonitorPeriod] 周末，不监控");
            return false;
        }

        // 检查分钟是否匹配
        if (_currentConfig.UseFixedTimePoints)
        {
            var minute = now.Minute;
            var fixedMinutes = _currentConfig.FixedTimeMinutes
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var m) ? m : -1)
                .Where(m => m >= 0 && m < 60)
                .ToList();
            SafeLog("DEBUG", $"[IsInMonitorPeriod] 固定时间点模式，配置的分钟: {string.Join(",", fixedMinutes)}, 当前分钟: {minute}");

            if (!fixedMinutes.Contains(minute))
                return false;
        }
        else
        {
            var minute = now.Minute;
            var interval = _currentConfig.MonitorIntervalMinutes;
            SafeLog("DEBUG", $"[IsInMonitorPeriod] 间隔模式，间隔={interval}分钟，当前分钟={minute}，{minute} % {interval} = {minute % interval}");
            if (minute % interval != 0)
                return false;
        }

        // 检查时间段
        if (TimeSpan.TryParse(_currentConfig.MonitorStartTime, out var startTime) &&
            TimeSpan.TryParse(_currentConfig.MonitorEndTime, out var endTime))
        {
            var currentTime = now.TimeOfDay;
            SafeLog("DEBUG", $"[IsInMonitorPeriod] 时间段: {startTime} - {endTime}, 当前: {currentTime}");
            if (currentTime >= startTime && currentTime <= endTime)
                return true;
        }

        SafeLog("DEBUG", "[IsInMonitorPeriod] 不在时间段内或时间格式错误");
        return false;
    }

    private async Task<Newtonsoft.Json.Linq.JToken?> FetchSignalDataAsync()
    {
        if (_currentConfig == null) return null;

        try
        {
            // 股票API直接使用配置的完整地址，不需要添加Strategys参数
            var fullUrl = _currentConfig.ApiBaseUrl.Trim();
            if (!fullUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !fullUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                fullUrl = "http://" + fullUrl;
            }

            SafeLog("INFO", $"调用API: {fullUrl}");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrWhiteSpace(_currentConfig.ApiToken))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _currentConfig.ApiToken);
            }

            var response = await client.GetAsync(fullUrl);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                SafeLog("INFO", $"API返回数据长度: {content.Length} 字节");
                return Newtonsoft.Json.Linq.JToken.Parse(content);
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                SafeLog("ERROR", $"API返回错误: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            SafeLog("ERROR", $"获取信号数据异常: {ex.Message}");
        }

        return null;
    }

    private List<Services.StrategyTriggerResult> CheckConditions(Newtonsoft.Json.Linq.JToken data)
    {
        var results = new List<Services.StrategyTriggerResult>();

        if (_currentConfig == null) return results;

        var enabledStrategies = GetEnabledStrategies();
        SafeLog("DEBUG", $"CheckConditions: 启用的策略={string.Join(",", enabledStrategies)}");
        
        if (enabledStrategies.Count == 0)
        {
            SafeLog("WARN", "没有勾选任何主要监控策略");
            return results;
        }

        var dataArray = data["data"] as Newtonsoft.Json.Linq.JArray;
        if (dataArray == null)
        {
            SafeLog("WARN", "API返回数据中没有 data 数组");
            return results;
        }
        SafeLog("DEBUG", $"CheckConditions: API返回 {dataArray.Count} 个品种");

        bool checkRealTimeStop = _currentConfig.EnableRealTimeStopPriceDiffRateCondition;
        bool checkRemainingRisk = _currentConfig.EnableRemainingRiskCondition;
        double realTimeStopThreshold = _currentConfig.RealTimeStopPriceDiffRateValue / 100.0;
        double remainingRiskThreshold = _currentConfig.RemainingRiskValue / 100.0;
        SafeLog("DEBUG", $"CheckConditions: checkRealTimeStop={checkRealTimeStop}, threshold={realTimeStopThreshold}, checkRemainingRisk={checkRemainingRisk}, riskThreshold={remainingRiskThreshold}");

        foreach (var currentStrategy in enabledStrategies)
        {
            var higherStrategies = GetHigherStrategies(currentStrategy);
            SafeLog("DEBUG", $"CheckConditions: 策略={currentStrategy}, 高级策略={string.Join(",", higherStrategies)}");
            
            var triggeredProducts = new List<Services.ProductTriggerInfo>();
            var candidateProducts = new List<(string productId, string direction, double realTimeStop, double remainingRisk, Newtonsoft.Json.Linq.JObject items)>();

            foreach (var productData in dataArray)
            {
                var productId = productData["productId"]?.ToString();
                if (string.IsNullOrEmpty(productId)) continue;

                var items = productData["items"] as Newtonsoft.Json.Linq.JObject;
                if (items == null) continue;

                var currentStrategyData = items[currentStrategy] as Newtonsoft.Json.Linq.JObject;
                if (currentStrategyData == null) continue;

                // direction: 1=多头，0=空/无持仓
                var directionValue = (int?)currentStrategyData["direction"] ?? -1;
                if (directionValue != 1)
                {
                    SafeLog("DEBUG", $"  {productId}.{currentStrategy}: direction={directionValue} (不是多头，跳过)");
                    continue;
                }

                var realTimeStopPriceDiffRate = (double?)currentStrategyData["realTimeStopPriceDiffRate"] ?? 0;

                if (checkRealTimeStop && realTimeStopPriceDiffRate <= realTimeStopThreshold)
                {
                    SafeLog("DEBUG", $"  {productId}.{currentStrategy}: realTimeStop={realTimeStopPriceDiffRate:P4} <= {realTimeStopThreshold:P4} (被阈值过滤)");
                    continue;
                }

                var remainingRisk = (double?)currentStrategyData["remainingRisk"] ?? 0;
                var direction = "多头";
                candidateProducts.Add((productId, direction, realTimeStopPriceDiffRate, remainingRisk, items));
                SafeLog("DEBUG", $"  {productId}.{currentStrategy}: 候选！direction=多头, realTimeStop={realTimeStopPriceDiffRate:P4}, remainingRisk={remainingRisk:P4}");
            }

            SafeLog("DEBUG", $"CheckConditions: {currentStrategy} 候选品种数={candidateProducts.Count}");

            if (candidateProducts.Count == 0) continue;

            foreach (var candidate in candidateProducts)
            {
                bool allMatch = true;
                var failedReason = "";

                foreach (var higherStrategy in higherStrategies)
                {
                    var higherStrategyData = candidate.items[higherStrategy] as Newtonsoft.Json.Linq.JObject;
                    if (higherStrategyData == null)
                    {
                        allMatch = false;
                        failedReason = $"{higherStrategy}数据不存在";
                        break;
                    }

                    // 高策略也必须是多头持仓
                    var higherDirValue = (int?)higherStrategyData["direction"] ?? -1;
                    if (higherDirValue != 1)
                    {
                        allMatch = false;
                        failedReason = $"{higherStrategy}.direction={higherDirValue}";
                        break;
                    }
                }

                if (!allMatch)
                {
                    SafeLog("DEBUG", $"  {candidate.productId}.{currentStrategy}: 高级策略检查失败 - {failedReason}");
                    continue;
                }

                var lastHigherStrategy = higherStrategies.LastOrDefault();
                double highestRisk = 0;
                if (!string.IsNullOrEmpty(lastHigherStrategy) && candidate.items[lastHigherStrategy] is Newtonsoft.Json.Linq.JObject lastHigherData)
                {
                    highestRisk = (double?)lastHigherData["remainingRisk"] ?? 0;
                }

                triggeredProducts.Add(new Services.ProductTriggerInfo
                {
                    ProductId = candidate.productId,
                    CurrentDirection = candidate.direction,
                    RealTimeStopPriceDiffRate = candidate.realTimeStop,
                    RemainingRisk = highestRisk > 0 ? highestRisk : candidate.remainingRisk
                });
                SafeLog("DEBUG", $"  {candidate.productId}.{currentStrategy}: ✅ 满足条件! risk={triggeredProducts.Last().RemainingRisk:P4}");
            }

            if (checkRemainingRisk && triggeredProducts.Count > 0)
            {
                var before = triggeredProducts.Count;
                triggeredProducts = triggeredProducts.Where(p => p.RemainingRisk > remainingRiskThreshold).ToList();
                SafeLog("DEBUG", $"CheckConditions: remainingRisk 过滤 {before} -> {triggeredProducts.Count} (threshold={remainingRiskThreshold:P4})");
            }

            if (triggeredProducts.Count > 0)
            {
                results.Add(new Services.StrategyTriggerResult
                {
                    StrategyName = currentStrategy,
                    Products = triggeredProducts
                });
                SafeLog("DEBUG", $"CheckConditions: {currentStrategy} 最终满足条件 {triggeredProducts.Count} 个品种");
            }
        }

        SafeLog("DEBUG", $"CheckConditions: 最终返回 {results.Count} 个策略");
        return results;
    }

    private List<string> GetEnabledStrategies()
    {
        var strategies = new List<string>();
        if (_currentConfig == null) return strategies;

        if (_currentConfig.EnableGD15) strategies.Add("GD15");
        if (_currentConfig.EnableGD20) strategies.Add("GD20");
        if (_currentConfig.EnableGD25) strategies.Add("GD25");
        if (_currentConfig.EnableGD30) strategies.Add("GD30");

        return strategies;
    }

    private List<string> GetHigherStrategies(string mainStrategy)
    {
        // 股票只有 GD15-GD30
        var allStrategies = new[] { "GD15", "GD20", "GD25", "GD30" };
        var index = Array.IndexOf(allStrategies, mainStrategy);
        if (index < 0 || index >= allStrategies.Length - 1)
            return new List<string>();

        return allStrategies.Skip(index + 1).ToList();
    }

    private async Task PushNotificationAsync(string textContent, string? rawData = null)
    {
        if (_currentConfig == null) return;

        try
        {
            SafeLog("INFO", $"PushNotificationAsync 被调用");
            SafeLog("INFO", $"配置检查 - EnableText={_currentConfig.EnableText}, EnableImage={_currentConfig.EnableImage}, TerminalId={_currentConfig.TerminalId}");
            
            var terminalIds = string.IsNullOrEmpty(_currentConfig.TerminalId) 
                ? new List<string>() 
                : _currentConfig.TerminalId.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();

            if (terminalIds.Count == 0)
            {
                SafeLog("WARN", "未配置推送终端");
                return;
            }

            // 发送文本消息
            if (_currentConfig.EnableText && !string.IsNullOrEmpty(textContent))
            {
                SafeLog("INFO", "开始推送文本消息...");
                await PushTextToFeishuAsync(textContent, terminalIds);
                SafeLog("INFO", "文本消息推送完成");
            }
            
            // 发送图片消息
            if (_currentConfig.EnableImage && !string.IsNullOrEmpty(rawData))
            {
                SafeLog("INFO", "开始推送图片消息...");
                await PushImageToFeishuAsync(rawData, terminalIds);
                SafeLog("INFO", "图片消息推送完成");
            }
        }
        catch (Exception ex)
        {
            SafeLog("ERROR", $"推送通知失败: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private async Task PushTextToFeishuAsync(string content, List<string> terminalIds)
    {
        try
        {
            var terminals = _configService.Config.FeishuPushConfig.Configs;
            
            foreach (var terminalId in terminalIds)
            {
                var terminal = terminals.FirstOrDefault(t => t.TerminalId == terminalId);
                if (terminal == null || string.IsNullOrEmpty(terminal.TextWebhook))
                {
                    SafeLog("WARN", $"终端 [{terminalId}] Webhook 未配置");
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
                    SafeLog("INFO", $"文本消息发送到 [{terminalId}] 成功");
                }
                else
                {
                    SafeLog("ERROR", $"文本消息发送到 [{terminalId}] 失败: HTTP {response.StatusCode}");
                }
            }
        }
        catch (Exception ex)
        {
            SafeLog("ERROR", $"发送文本消息异常: {ex.Message}");
        }
    }

    private async Task PushImageToFeishuAsync(string rawData, List<string> terminalIds)
    {
        try
        {
            // 生成图片
            SafeLog("INFO", $"[PushImage] 开始生成图片，原始数据长度: {rawData.Length}");
            var imageData = _imageGenerator.GenerateStockGDSignalImage(rawData, "股票GD信号");
            if (imageData == null)
            {
                SafeLog("ERROR", "[PushImage] 图片生成失败，返回 null");
                return;
            }
            SafeLog("INFO", $"[PushImage] 图片生成成功，大小: {imageData.Length} bytes");

            var terminals = _configService.Config.FeishuPushConfig.Configs;

            foreach (var terminalId in terminalIds)
            {
                var terminal = terminals.FirstOrDefault(t => t.TerminalId == terminalId);
                if (terminal == null)
                {
                    SafeLog("WARN", $"[PushImage] 终端 [{terminalId}] 配置不存在");
                    continue;
                }

                SafeLog("INFO", $"[PushImage] 终端配置检查 - ImageApiKey: {(string.IsNullOrEmpty(terminal.ImageApiKey) ? "空" : "有")}, ImageSecretKey: {(string.IsNullOrEmpty(terminal.ImageSecretKey) ? "空" : "有")}, ImageReceiverId: {(string.IsNullOrEmpty(terminal.ImageReceiverId) ? "空" : "有")}");

                if (string.IsNullOrEmpty(terminal.ImageApiKey) || 
                    string.IsNullOrEmpty(terminal.ImageSecretKey) || 
                    string.IsNullOrEmpty(terminal.ImageReceiverId))
                {
                    SafeLog("WARN", $"[PushImage] 终端 [{terminalId}] 图片推送配置不完整");
                    continue;
                }

                // 获取 Access Token
                SafeLog("INFO", $"[PushImage] 获取 Access Token...");
                var token = await GetFeishuAccessTokenAsync(terminal.ImageApiKey, terminal.ImageSecretKey);
                if (string.IsNullOrEmpty(token))
                {
                    SafeLog("ERROR", $"[PushImage] 获取 Access Token 失败");
                    continue;
                }
                SafeLog("INFO", $"[PushImage] Access Token 获取成功");

                // 上传图片
                SafeLog("INFO", $"[PushImage] 上传图片到飞书...");
                var imageKey = await UploadImageToFeishuAsync(imageData, token);
                if (string.IsNullOrEmpty(imageKey))
                {
                    SafeLog("ERROR", $"[PushImage] 图片上传失败");
                    continue;
                }
                SafeLog("INFO", $"[PushImage] 图片上传成功，imageKey: {imageKey}");

                // 发送图片消息
                SafeLog("INFO", $"[PushImage] 发送图片消息...");
                var success = await SendImageMessageToFeishuAsync(terminal.ImageReceiverId, imageKey, token);
                if (success)
                {
                    SafeLog("INFO", $"[PushImage] ✅ 图片消息发送到 [{terminalId}] 成功");
                }
                else
                {
                    SafeLog("ERROR", $"[PushImage] ❌ 图片消息发送到 [{terminalId}] 失败");
                }
            }
        }
        catch (Exception ex)
        {
            SafeLog("ERROR", $"[PushImage] 发送图片消息异常: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private async Task<string?> GetFeishuAccessTokenAsync(string appId, string appSecret)
    {
        try
        {
            using var client = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Post, "https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal");
            
            var content = new Dictionary<string, string>
            {
                { "app_id", appId },
                { "app_secret", appSecret }
            };
            request.Content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(content), Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var result = Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json);
                return result?["tenant_access_token"]?.ToString();
            }
        }
        catch (Exception ex)
        {
            SafeLog("ERROR", $"获取 Access Token 异常: {ex.Message}");
        }
        return null;
    }

    private async Task<string?> UploadImageToFeishuAsync(byte[] imageData, string token)
    {
        try
        {
            using var client = new HttpClient();
            using var form = new MultipartFormDataContent();

            var imageContent = new ByteArrayContent(imageData);
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(imageContent, "image", "signal.png");
            form.Add(new StringContent("message"), "image_type");

            var request = new HttpRequestMessage(HttpMethod.Post, "https://open.feishu.cn/open-apis/im/v1/images");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Content = form;

            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var result = Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json);
                return result?["data"]?["image_key"]?.ToString();
            }
            else
            {
                SafeLog("ERROR", $"上传图片失败: HTTP {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            SafeLog("ERROR", $"上传图片异常: {ex.Message}");
        }
        return null;
    }

    private async Task<bool> SendImageMessageToFeishuAsync(string receiveId, string imageKey, string token)
    {
        try
        {
            using var client = new HttpClient();
            
            // content 必须是 JSON 字符串
            var contentObj = new { image_key = imageKey };
            
            // 根据 receiveId 类型选择 receive_id_type
            var idType = receiveId.StartsWith("oc_") || receiveId.StartsWith("chat_") ? "chat_id" : "open_id";
            
            var message = new
            {
                receive_id = receiveId,
                msg_type = "image",
                content = Newtonsoft.Json.JsonConvert.SerializeObject(contentObj)
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"https://open.feishu.cn/open-apis/im/v1/messages?receive_id_type={idType}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(message), Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();
            SafeLog("INFO", $"[SendImage] receive_id_type={idType}, 响应状态: {response.StatusCode}, 内容: {responseContent}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            SafeLog("ERROR", $"发送图片消息异常: {ex.Message}");
            return false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isDisposing = true;
        StopMonitorTimer();

        if (_currentConfig != null)
        {
            SaveConfigFromUI();
            _currentConfig.IsEnabled = _isMonitoring;
            _databaseService.SaveGDStockSignalConfig(_currentConfig);
        }

        base.OnClosed(e);
    }
}
