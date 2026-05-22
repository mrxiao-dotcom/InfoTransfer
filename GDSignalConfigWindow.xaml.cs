using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using InfoTransfer.Models;
using InfoTransfer.Services;

namespace InfoTransfer;

public partial class GDSignalConfigWindow : Window
{
    private readonly DatabaseService _databaseService;
    private readonly ConfigService _configService;
    private readonly Action<string, string> _logCallback;
    private GDSignalConfig? _currentConfig;
    private readonly ObservableCollection<ConditionItem> _conditions;
    private System.Timers.Timer? _monitorTimer;
    private bool _isMonitoring;
    private bool _isDisposing;
    private readonly string _logFilePath;
    private readonly object _logFileLock = new();
    private Action<string, string> _safeLogCallback = null!;
    private System.Threading.Timer? _autoSaveTimer;
    private readonly object _autoSaveLock = new();
    private List<CheckBox> _terminalCheckBoxes = new();

    public GDSignalConfigWindow(DatabaseService databaseService, ConfigService configService, Action<string, string> logCallback)
    {
        InitializeComponent();

        _databaseService = databaseService;
        _configService = configService;
        _logCallback = logCallback;
        
        // GD监控相关日志写入文件，不输出到控制台
        _safeLogCallback = (level, message) =>
        {
            WriteToLogFile(level, message);
        };

        _conditions = new ObservableCollection<ConditionItem>();

        // 初始化日志文件路径
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var logDir = Path.Combine(appDir, "logs");
        if (!Directory.Exists(logDir))
        {
            Directory.CreateDirectory(logDir);
        }
        _logFilePath = Path.Combine(logDir, $"GDSignal_{DateTime.Now:yyyyMMdd}.log");

        // 在窗口加载完成后初始化
        Loaded += GDSignalConfigWindow_Loaded;
    }

    private void GDSignalConfigWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadOrCreateConfig();
        LoadTerminals(); // LoadTerminals 需要在 LoadOrCreateConfig 之后，因为依赖 _currentConfig
        UpdateUIState();
        BindAutoSaveEvents();
    }

    /// <summary>
    /// 绑定所有控件的自动保存事件
    /// </summary>
    private void BindAutoSaveEvents()
    {
        // TextBox 失去焦点时自动保存
        TxtApiBaseUrl.LostFocus += (s, e) => ScheduleAutoSave();
        TxtApiToken.LostFocus += (s, e) => ScheduleAutoSave();
        TxtDayStartTime.LostFocus += (s, e) => ScheduleAutoSave();
        TxtDayEndTime.LostFocus += (s, e) => ScheduleAutoSave();
        TxtNightStartTime.LostFocus += (s, e) => ScheduleAutoSave();
        TxtNightEndTime.LostFocus += (s, e) => ScheduleAutoSave();
        TxtFixedTimeMinutes.LostFocus += (s, e) => ScheduleAutoSave();
        TxtIntervalMinutes.LostFocus += (s, e) => ScheduleAutoSave();
        TxtRealTimeStopValue.LostFocus += (s, e) => ScheduleAutoSave();
        TxtRemainingRiskValue.LostFocus += (s, e) => ScheduleAutoSave();

        // CheckBox 变化时自动保存
        ChkGD15.Checked += (s, e) => ScheduleAutoSave();
        ChkGD15.Unchecked += (s, e) => ScheduleAutoSave();
        ChkGD20.Checked += (s, e) => ScheduleAutoSave();
        ChkGD20.Unchecked += (s, e) => ScheduleAutoSave();
        ChkGD25.Checked += (s, e) => ScheduleAutoSave();
        ChkGD25.Unchecked += (s, e) => ScheduleAutoSave();
        ChkGD30.Checked += (s, e) => ScheduleAutoSave();
        ChkGD30.Unchecked += (s, e) => ScheduleAutoSave();
        ChkGD35.Checked += (s, e) => ScheduleAutoSave();
        ChkGD35.Unchecked += (s, e) => ScheduleAutoSave();
        ChkGD40.Checked += (s, e) => ScheduleAutoSave();
        ChkGD40.Unchecked += (s, e) => ScheduleAutoSave();
        ChkNightSession.Checked += (s, e) => ScheduleAutoSave();
        ChkNightSession.Unchecked += (s, e) => ScheduleAutoSave();
        ChkEnableText.Checked += (s, e) => ScheduleAutoSave();
        ChkEnableText.Unchecked += (s, e) => ScheduleAutoSave();
        ChkEnableImage.Checked += (s, e) => ScheduleAutoSave();
        ChkEnableImage.Unchecked += (s, e) => ScheduleAutoSave();

        // RadioButton 变化时自动保存
        RbFixedTime.Checked += (s, e) => ScheduleAutoSave();
        RbInterval.Checked += (s, e) => ScheduleAutoSave();

        // TerminalCheckBoxes will be bound in LoadTerminals after controls are initialized
    }

    /// <summary>
    /// 延迟自动保存（500ms后执行）
    /// </summary>
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

    /// <summary>
    /// 立即保存配置到数据库
    /// </summary>
    private void SaveConfigToDatabase()
    {
        if (_currentConfig == null) return;

        try
        {
            _databaseService.SaveGDSignalConfig(_currentConfig);
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
        // 加载已有的GD监控配置（目前只支持一个配置）
        var configs = _databaseService.GetAllGDSignalConfigs();
        if (configs.Count > 0)
        {
            _currentConfig = configs[0];
            LoadConfigToUI();
        }
        else
        {
            _currentConfig = new GDSignalConfig();
        }

        RefreshConditionList();
    }

    private void LoadConfigToUI()
    {
        if (_currentConfig == null) return;

        TxtApiBaseUrl.Text = _currentConfig.ApiBaseUrl;
        TxtApiToken.Text = _currentConfig.ApiToken;
        TxtDayStartTime.Text = _currentConfig.MonitorStartTime;
        TxtDayEndTime.Text = _currentConfig.MonitorEndTime;
        ChkNightSession.IsChecked = _currentConfig.MonitorNightSession;
        TxtNightStartTime.Text = string.IsNullOrEmpty(_currentConfig.NightSessionStartTime) ? "21:00" : _currentConfig.NightSessionStartTime;
        TxtNightEndTime.Text = string.IsNullOrEmpty(_currentConfig.NightSessionEndTime) ? "02:30" : _currentConfig.NightSessionEndTime;
        TxtNightStartTime.IsEnabled = _currentConfig.MonitorNightSession;
        TxtNightEndTime.IsEnabled = _currentConfig.MonitorNightSession;
        TxtIntervalMinutes.Text = _currentConfig.MonitorIntervalMinutes.ToString();
        RbFixedTime.IsChecked = _currentConfig.UseFixedTimePoints;
        RbInterval.IsChecked = !_currentConfig.UseFixedTimePoints;
        TxtIntervalMinutes.IsEnabled = !_currentConfig.UseFixedTimePoints;
        TxtFixedTimeMinutes.IsEnabled = _currentConfig.UseFixedTimePoints;
        TxtFixedTimeMinutes.Text = string.IsNullOrEmpty(_currentConfig.FixedTimeMinutes) ? "0,15,30,45" : _currentConfig.FixedTimeMinutes;

        ChkEnableText.IsChecked = _currentConfig.EnableText;
        ChkEnableImage.IsChecked = _currentConfig.EnableImage;

        // 加载策略勾选状态
        ChkGD15.IsChecked = _currentConfig.EnableGD15;
        ChkGD20.IsChecked = _currentConfig.EnableGD20;
        ChkGD25.IsChecked = _currentConfig.EnableGD25;
        ChkGD30.IsChecked = _currentConfig.EnableGD30;
        ChkGD35.IsChecked = _currentConfig.EnableGD35;
        ChkGD40.IsChecked = _currentConfig.EnableGD40;

        TxtRealTimeStopValue.Text = _currentConfig.RealTimeStopPriceDiffRateValue.ToString();
        TxtRemainingRiskValue.Text = _currentConfig.RemainingRiskValue.ToString();

        _isMonitoring = _currentConfig.IsEnabled;
        UpdateUIState();
    }

    private void SaveConfigFromUI()
    {
        if (_currentConfig == null)
        {
            _currentConfig = new GDSignalConfig();
        }

        _currentConfig.ApiBaseUrl = TxtApiBaseUrl.Text.Trim();
        _currentConfig.ApiToken = TxtApiToken.Text.Trim();
        _currentConfig.MonitorStartTime = TxtDayStartTime.Text.Trim();
        _currentConfig.MonitorEndTime = TxtDayEndTime.Text.Trim();
        _currentConfig.MonitorNightSession = ChkNightSession.IsChecked == true;
        _currentConfig.NightSessionStartTime = TxtNightStartTime.Text.Trim();
        _currentConfig.NightSessionEndTime = TxtNightEndTime.Text.Trim();
        _currentConfig.MonitorIntervalMinutes = int.TryParse(TxtIntervalMinutes.Text, out var interval) ? interval : 30;
        _currentConfig.UseFixedTimePoints = RbFixedTime.IsChecked == true;
        _currentConfig.FixedTimeMinutes = TxtFixedTimeMinutes.Text.Trim();

        // 保存选中的终端ID列表（逗号分隔）
        var selectedTerminals = _terminalCheckBoxes
            .Where(cb => cb.IsChecked == true)
            .Select(cb => cb.Tag?.ToString())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
        _currentConfig.TerminalId = string.Join(",", selectedTerminals);

        _currentConfig.EnableText = ChkEnableText.IsChecked == true;
        _currentConfig.EnableImage = ChkEnableImage.IsChecked == true;
        _currentConfig.IsEnabled = _isMonitoring;

        // 保存策略勾选状态
        _currentConfig.EnableGD15 = ChkGD15.IsChecked == true;
        _currentConfig.EnableGD20 = ChkGD20.IsChecked == true;
        _currentConfig.EnableGD25 = ChkGD25.IsChecked == true;
        _currentConfig.EnableGD30 = ChkGD30.IsChecked == true;
        _currentConfig.EnableGD35 = ChkGD35.IsChecked == true;
        _currentConfig.EnableGD40 = ChkGD40.IsChecked == true;

        _currentConfig.RealTimeStopPriceDiffRateValue = double.TryParse(TxtRealTimeStopValue.Text, out var stopVal) ? stopVal : 0;
        _currentConfig.RemainingRiskValue = double.TryParse(TxtRemainingRiskValue.Text, out var riskVal) ? riskVal : 0;

        _currentConfig.UpdatedAt = DateTime.Now;
    }

    private void RefreshConditionList()
    {
        // 条件现在固定，无需刷新
    }

    private void UpdateUIState()
    {
        // 检查是否已经关闭
        if (_isDisposing) return;

        try
        {
            if (_isMonitoring)
            {
                StatusIndicator?.Dispatcher.Invoke(() =>
                {
                    StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#52C41A"));
                });
                TxtStatus?.Dispatcher.Invoke(() => { TxtStatus.Text = "运行中"; });
                BtnStartMonitor?.Dispatcher.Invoke(() => { BtnStartMonitor.IsEnabled = false; });
                BtnStopMonitor?.Dispatcher.Invoke(() => { BtnStopMonitor.IsEnabled = true; });
            }
            else
            {
                StatusIndicator?.Dispatcher.Invoke(() =>
                {
                    StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D9D9D9"));
                });
                TxtStatus?.Dispatcher.Invoke(() => { TxtStatus.Text = "已停止"; });
                BtnStartMonitor?.Dispatcher.Invoke(() => { BtnStartMonitor.IsEnabled = true; });
                BtnStopMonitor?.Dispatcher.Invoke(() => { BtnStopMonitor.IsEnabled = false; });
            }

            TxtFixedTimeHint?.Dispatcher.Invoke(() =>
            {
                if (TxtFixedTimeHint != null)
                {
                    TxtFixedTimeHint.Text = RbFixedTime.IsChecked == true
                        ? "将在每小时的 00、15、30、45 分进行数据检查"
                        : "将在每个整点和半点进行检查";
                }
            });
        }
        catch (ObjectDisposedException)
        {
            // 窗口已关闭
        }
        catch (Exception)
        {
            // 忽略其他异常
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

    private void ChkNightSession_Changed(object sender, RoutedEventArgs e)
    {
        if (TxtNightStartTime == null) return;
        var isEnabled = ChkNightSession.IsChecked == true;
        TxtNightStartTime.IsEnabled = isEnabled;
        TxtNightEndTime.IsEnabled = isEnabled;
    }

    private void BtnAddCondition_Click(object sender, RoutedEventArgs e)
    {
        _conditions.Add(new ConditionItem
        {
            Field = "stopPriceDiffRate",
            Operator = "<",
            Value = 0.05,
            LogicOperator = "and"
        });
        RefreshConditionList();
    }

    private void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveConfigFromUI();
            _databaseService.SaveGDSignalConfig(_currentConfig!);
            _safeLogCallback("INFO", "GD监控配置已保存");
            MessageBox.Show("配置已保存", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _safeLogCallback("ERROR", $"保存配置失败: {ex.Message}");
            MessageBox.Show($"保存配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnTestApi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 从消息源配置中获取 ID=4 的 API 设置
            var messageSource = _databaseService.GetMessageSourceBySourceId("4");
            if (messageSource == null)
            {
                MessageBox.Show("未找到消息源 ID=4 的配置", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Dispatcher.Invoke(() =>
            {
                BtnTestApi.IsEnabled = false;
                BtnTestApi.Content = "测试中...";
            });

            // 构建 API URL
            var apiUrl = messageSource.ApiUrl;
            var strategys = GetEnabledMainStrategies();
            bool urlHasStrategys = apiUrl.Contains("Strategys=");

            string fullUrl = apiUrl;
            if (!urlHasStrategys && strategys.Count > 0)
            {
                var paramList = new List<string>();
                foreach (var s in strategys)
                {
                    paramList.Add($"Strategys={Uri.EscapeDataString(s)}");
                }
                if (paramList.Count > 0)
                {
                    fullUrl += (apiUrl.Contains('?') ? "&" : "?") + string.Join("&", paramList);
                }
            }

            _safeLogCallback("INFO", $"测试API连接: {fullUrl}");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrWhiteSpace(messageSource.ApiToken))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", messageSource.ApiToken);
            }

            var response = await client.GetAsync(fullUrl);
            var content = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _safeLogCallback("INFO", "API连接成功!");
                MessageBox.Show("API连接成功!", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                _safeLogCallback("ERROR", $"API返回错误: {response.StatusCode}");
                MessageBox.Show($"API返回错误: {response.StatusCode}\n{content}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            _safeLogCallback("ERROR", $"API连接失败: {ex.Message}");
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
        StartMonitoring();
    }

    private void BtnStopMonitor_Click(object sender, RoutedEventArgs e)
    {
        StopMonitoring();
    }

    private async void BtnRealtimePush_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnRealtimePush.IsEnabled = false;
            _safeLogCallback("INFO", "========== 开始实时推送 ==========");

            // 获取所有GD监控配置
            var configs = _databaseService.GetAllGDSignalConfigs();
            if (configs.Count == 0)
            {
                _safeLogCallback("WARN", "未找到GD监控配置");
                return;
            }

            // 只调用一次 API 获取所有数据
            var data = await FetchSignalDataAsync();
            if (data == null)
            {
                _safeLogCallback("ERROR", "API调用失败");
                return;
            }

            var dataArray = data["data"] as JArray;
            if (dataArray == null)
            {
                _safeLogCallback("ERROR", "数据格式错误");
                return;
            }

            foreach (var config in configs)
            {
                // 显示配置及其启用的策略
                var enabledStrategies = new List<string>();
                if (config.EnableGD15) enabledStrategies.Add("GD15");
                if (config.EnableGD20) enabledStrategies.Add("GD20");
                if (config.EnableGD25) enabledStrategies.Add("GD25");
                if (config.EnableGD30) enabledStrategies.Add("GD30");
                if (config.EnableGD35) enabledStrategies.Add("GD35");
                if (config.EnableGD40) enabledStrategies.Add("GD40");

                _safeLogCallback("INFO", $"处理配置: {config.Name} (ID={config.Id}), 启用策略: {(enabledStrategies.Count > 0 ? string.Join(",", enabledStrategies) : "无")}");

                if (enabledStrategies.Count == 0)
                {
                    _safeLogCallback("INFO", $"  -> 该配置没有启用任何策略，跳过");
                    continue;
                }

                // 收集每个策略下 realTimeStopPriceDiffRate > 0 的品种
                var strategyProducts = new Dictionary<string, List<string>>();
                foreach (var strategy in enabledStrategies)
                {
                    strategyProducts[strategy] = new List<string>();
                }

                foreach (var productData in dataArray)
                {
                    var productId = productData["productId"]?.ToString() ?? "";
                    var items = productData["items"] as JObject;
                    if (items == null) continue;

                    foreach (var strategyName in enabledStrategies)
                    {
                        var strategyData = items[strategyName] as JObject;
                        if (strategyData == null) continue;

                        var realTimeStopPriceDiffRate = strategyData["realTimeStopPriceDiffRate"]?.Value<double>() ?? 0;
                        if (realTimeStopPriceDiffRate > 0)
                        {
                            strategyProducts[strategyName].Add(productId);
                        }
                    }
                }

                // 输出表格：行为品种，列为策略
                var allProducts = strategyProducts.Values.SelectMany(x => x).Distinct().OrderBy(x => x).ToList();

                if (allProducts.Count == 0)
                {
                    _safeLogCallback("INFO", $"【{config.Name}】没有找到 realTimeStopPriceDiffRate > 0 的品种");
                    continue;
                }

                // 构建表头
                var header = string.Join(" | ", enabledStrategies.Select(s => s.PadRight(6)));
                _safeLogCallback("INFO", $"【{config.Name}】realTimeStopPriceDiffRate > 0 的品种 (策略列为策略)");
                _safeLogCallback("INFO", new string('-', header.Length));
                _safeLogCallback("INFO", header);
                _safeLogCallback("INFO", new string('-', header.Length));

                // 构建数据行
                foreach (var productId in allProducts)
                {
                    var cells = new List<string>();
                    foreach (var strategy in enabledStrategies)
                    {
                        var hasProduct = strategyProducts[strategy].Contains(productId);
                        cells.Add(hasProduct ? productId : "");
                    }
                    _safeLogCallback("INFO", string.Join(" | ", cells.Select(c => c.PadRight(6))));
                }
                _safeLogCallback("INFO", new string('-', header.Length));

                // 输出统计
                foreach (var strategy in enabledStrategies)
                {
                    var count = strategyProducts[strategy].Count;
                    if (count > 0)
                    {
                        _safeLogCallback("INFO", $"  {strategy}: {string.Join(", ", strategyProducts[strategy])}");
                    }
                }
            }

            _safeLogCallback("INFO", "========== 实时推送完成 ==========");
        }
        catch (Exception ex)
        {
            _safeLogCallback("ERROR", $"实时推送异常: {ex.Message}");
        }
        finally
        {
            BtnRealtimePush.IsEnabled = true;
        }
    }

    private void StartMonitorTimer()
    {
        StopMonitorTimer();

        // 根据配置的间隔设置定时器（转换为毫秒）
        var intervalMs = _currentConfig?.MonitorIntervalMinutes * 60 * 1000 ?? 60000;
        if (intervalMs < 10000) intervalMs = 10000; // 最小间隔10秒

        _monitorTimer = new System.Timers.Timer(intervalMs);
        _monitorTimer.Elapsed += (s, e) =>
        {
            try
            {
                if (_isMonitoring && !_isDisposing)
                {
                    CheckMonitorConditionsAsync().ConfigureAwait(false);
                }
            }
            catch { }
        };
        _monitorTimer.Start();

        _safeLogCallback("INFO", $"监控定时器已启动，间隔 {_currentConfig?.MonitorIntervalMinutes} 分钟");

        // 立即执行一次（使用 Task.Run 在后台线程执行）
        Task.Run(async () =>
        {
            if (!_isDisposing)
            {
                await CheckMonitorConditionsAsync();
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

    private async Task CheckMonitorConditionsAsync()
    {
        if (_currentConfig == null || !_isMonitoring || _isDisposing) return;

        try
        {
            _safeLogCallback("INFO", $"[{DateTime.Now:HH:mm:ss}] 开始检查GD策略信号...");

            // 获取API数据
            var data = await FetchSignalDataAsync();
            if (data == null)
            {
                _safeLogCallback("ERROR", "获取信号数据失败");
                return;
            }

            // 检查触发条件（详细判定过程已写入日志文件）
            var triggerResults = CheckConditions(data);
            int totalProducts = triggerResults.Sum(r => r.Products.Count);
            if (totalProducts > 0)
            {
                _safeLogCallback("INFO", $"检测到 {triggerResults.Count} 个策略共 {totalProducts} 个品种满足条件");
                await PushNotificationAsync(triggerResults, data);
            }
            else
            {
                _safeLogCallback("INFO", $"[{DateTime.Now:HH:mm:ss}] 运行中...");
            }
        }
        catch (Exception ex)
        {
            _safeLogCallback("ERROR", $"检查监控条件失败: {ex.Message}");
        }
    }

    private bool IsInMonitorPeriod()
    {
        if (_currentConfig == null) return false;

        var now = DateTime.Now;
        var dayOfWeek = now.DayOfWeek;

        // 周末不监控
        if (dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday)
        {
            return false;
        }

        if (_currentConfig.UseFixedTimePoints)
        {
            // 使用固定时间点模式，检查当前分钟是否在配置的时间点中
            var minute = now.Minute;
            var fixedMinutes = _currentConfig.FixedTimeMinutes
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var m) ? m : -1)
                .Where(m => m >= 0 && m < 60)
                .ToList();

            if (!fixedMinutes.Contains(minute))
            {
                return false;
            }
        }
        else
        {
            // 使用间隔模式
            var minute = now.Minute;
            if (minute % _currentConfig.MonitorIntervalMinutes != 0)
            {
                return false;
            }
        }

        // 检查日盘时间段
        if (TimeSpan.TryParse(_currentConfig.MonitorStartTime, out var startTime) &&
            TimeSpan.TryParse(_currentConfig.MonitorEndTime, out var endTime))
        {
            var currentTime = now.TimeOfDay;
            if (currentTime >= startTime && currentTime <= endTime)
            {
                return true;
            }
        }

        // 检查夜盘时间段
        if (_currentConfig.MonitorNightSession)
        {
            var nightStartStr = string.IsNullOrEmpty(_currentConfig.NightSessionStartTime) ? "21:00" : _currentConfig.NightSessionStartTime;
            var nightEndStr = string.IsNullOrEmpty(_currentConfig.NightSessionEndTime) ? "02:30" : _currentConfig.NightSessionEndTime;

            if (TimeSpan.TryParse(nightStartStr, out var nightStart) &&
                TimeSpan.TryParse(nightEndStr, out var nightEnd))
            {
                var currentTime = now.TimeOfDay;

                if (nightStart > nightEnd)
                {
                    // 跨午夜的情况（如 21:00 - 02:30）
                    if (currentTime >= nightStart || currentTime <= nightEnd)
                    {
                        return true;
                    }
                }
                else
                {
                    // 不跨午夜的情况
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
        if (_currentConfig == null) return null;

        try
        {
            // 从消息源配置中获取 ID=4 的 API 设置
            var messageSource = _databaseService.GetMessageSourceBySourceId("4");
            if (messageSource == null)
            {
                _safeLogCallback("ERROR", "未找到消息源 ID=4 的配置");
                WriteToLogFile("ERROR", "未找到消息源 ID=4 的配置");
                return null;
            }

            // 构建 API URL
            var apiUrl = messageSource.ApiUrl;
            _safeLogCallback("INFO", $"消息源配置: ID=4, URL={apiUrl}, Token=***");
            
            // 检查 URL 是否已经包含策略参数
            bool urlHasStrategys = apiUrl.Contains("Strategys=");
            
            // 添加策略参数（如果 URL 中没有策略参数）
            string fullUrl = apiUrl;
            
            // 直接请求所有策略，确保向上判定能获取完整数据
            var allStrategys = new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" };
            
            if (!urlHasStrategys)
            {
                var paramList = new List<string>();
                foreach (var s in allStrategys)
                {
                    paramList.Add($"Strategys={Uri.EscapeDataString(s)}");
                }
                fullUrl += (apiUrl.Contains('?') ? "&" : "?") + string.Join("&", paramList);
            }

            _safeLogCallback("INFO", $"调用API: {fullUrl}");
            WriteToLogFile("INFO", $"调用API: {fullUrl}");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrWhiteSpace(messageSource.ApiToken))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", messageSource.ApiToken);
            }

            WriteToLogFile("DEBUG", $"准备发送GET请求到: {fullUrl}");
            WriteToLogFile("DEBUG", $"使用Bearer Token: {(string.IsNullOrWhiteSpace(messageSource.ApiToken) ? "无" : "有")}");
            
            try
            {
                var response = await client.GetAsync(fullUrl);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    _safeLogCallback("INFO", $"API返回数据长度: {content.Length} 字节");
                    WriteToLogFile("INFO", $"API返回数据长度: {content.Length} 字节");
                    return JToken.Parse(content);
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _safeLogCallback("ERROR", $"API返回错误: {response.StatusCode}");
                    WriteToLogFile("ERROR", $"API返回错误: {response.StatusCode} - {errorContent}");
                }
            }
            catch (Exception httpEx)
            {
                WriteToLogFile("ERROR", $"HTTP请求异常: {httpEx.GetType().Name}: {httpEx.Message}");
                if (httpEx.InnerException != null)
                {
                    WriteToLogFile("ERROR", $"InnerException: {httpEx.InnerException.GetType().Name}: {httpEx.InnerException.Message}");
                    if (httpEx.InnerException.InnerException != null)
                    {
                        WriteToLogFile("ERROR", $"InnerInnerException: {httpEx.InnerException.InnerException.GetType().Name}: {httpEx.InnerException.InnerException.Message}");
                    }
                }
                _safeLogCallback("ERROR", $"HTTP请求异常: {httpEx.GetType().Name}: {httpEx.Message}");
                if (httpEx.InnerException != null)
                {
                    _safeLogCallback("ERROR", $"InnerException: {httpEx.InnerException.GetType().Name}: {httpEx.InnerException.Message}");
                    if (httpEx.InnerException.InnerException != null)
                    {
                        _safeLogCallback("ERROR", $"InnerInnerException: {httpEx.InnerException.InnerException.GetType().Name}: {httpEx.InnerException.InnerException.Message}");
                    }
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            WriteToLogFile("ERROR", $"获取信号数据异常: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
            {
                WriteToLogFile("ERROR", $"InnerException: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                if (ex.InnerException.InnerException != null)
                {
                    WriteToLogFile("ERROR", $"InnerInnerException: {ex.InnerException.InnerException.GetType().Name}: {ex.InnerException.InnerException.Message}");
                }
            }
            _safeLogCallback("ERROR", $"获取信号数据异常: {ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }

    private void WriteToLogFile(string level, string message)
    {
        try
        {
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
            lock (_logFileLock)
            {
                File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
            }
            System.Diagnostics.Debug.WriteLine(logEntry);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"日志写入失败: {ex.Message}");
        }
    }

    private void WriteSignalDataToLogFile(JToken data)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"========== GD信号数据 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========");

            var dataArray = data["data"] as JArray;
            if (dataArray != null)
            {
                foreach (var productData in dataArray)
                {
                    var productId = productData["productId"]?.ToString();
                    var items = productData["items"] as JObject;

                    if (items != null)
                    {
                        foreach (var strategyEntry in items)
                        {
                            var strategyName = strategyEntry.Key;
                            var strategyData = strategyEntry.Value as JObject;
                            if (strategyData != null)
                            {
                                var realTimeStopPriceDiffRate = strategyData["realTimeStopPriceDiffRate"]?.Value<double>() ?? 0;
                                var direction = strategyData["direction"]?.ToString() ?? "";
                                var tickTime = strategyData["tickTime"]?.ToString() ?? "";
                                sb.AppendLine($"{productId} | {strategyName} | direction: {direction} | tickTime: {tickTime} | realTimeStopPriceDiffRate: {realTimeStopPriceDiffRate:F6}");
                            }
                        }
                    }
                }
            }

            sb.AppendLine("=============================================");
            WriteToLogFile("DATA", sb.ToString());
        }
        catch (Exception ex)
        {
            WriteToLogFile("ERROR", $"写入信号数据到日志失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取所有已勾选的主要监控策略（按数字排序）
    /// </summary>
    private List<string> GetEnabledMainStrategies()
    {
        var strategies = new List<string>();
        if (_currentConfig == null) return strategies;

        if (_currentConfig.EnableGD15) strategies.Add("GD15");
        if (_currentConfig.EnableGD20) strategies.Add("GD20");
        if (_currentConfig.EnableGD25) strategies.Add("GD25");
        if (_currentConfig.EnableGD30) strategies.Add("GD30");
        if (_currentConfig.EnableGD35) strategies.Add("GD35");
        if (_currentConfig.EnableGD40) strategies.Add("GD40");

        return strategies;
    }

    /// <summary>
    /// 根据策略名称获取更高的策略列表
    /// </summary>
    private List<string> GetHigherStrategies(string mainStrategy)
    {
        var allStrategies = new[] { "GD15", "GD20", "GD25", "GD30", "GD35", "GD40" };
        var index = Array.IndexOf(allStrategies, mainStrategy);
        if (index < 0 || index >= allStrategies.Length - 1)
            return new List<string>();

        return allStrategies.Skip(index + 1).ToList();
    }

    /// <summary>
    /// 按策略分组的结果
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

    private List<StrategyTriggerResult> CheckConditions(JToken data)
    {
        var results = new List<StrategyTriggerResult>();

        if (_currentConfig == null) return results;

        var enabledStrategies = GetEnabledMainStrategies();
        if (enabledStrategies.Count == 0)
        {
            _safeLogCallback("WARN", "没有勾选任何主要监控策略");
            return results;
        }

        var dataArray = data["data"] as JArray;
        if (dataArray == null) return results;

        // 写入API返回数据的结构信息（用于调试）
        WriteToLogFile("CHECK", $"API返回数据: 共 {dataArray.Count} 个品种");
        if (dataArray.Count > 0)
        {
            var firstProduct = dataArray[0];
            var firstProductId = firstProduct["productId"]?.ToString() ?? "unknown";
            var firstItems = firstProduct["items"] as JObject;
            if (firstItems != null)
            {
                WriteToLogFile("CHECK", $"第一个品种 {firstProductId} 的items包含字段:");
                foreach (var prop in firstItems.Properties())
                {
                    var value = prop.Value.ToString();
                    if (value.Length > 100) value = value.Substring(0, 100) + "...";
                    WriteToLogFile("CHECK", $"  - {prop.Name}: {value}");
                }
            }
            else
            {
                WriteToLogFile("CHECK", $"第一个品种 {firstProductId} 没有items数据");
            }
        }

        // 条件固定为 > 0，不读取配置
        bool checkRealTimeStop = true;
        bool checkRemainingRisk = true;
        double realTimeStopThreshold = 0;  // > 0
        double remainingRiskThreshold = 0; // <= 0 (实际是 > 0 保留)

        // 写入详细判定日志
        WriteToLogFile("CHECK", $"========== 开始策略判定 ==========");
        WriteToLogFile("CHECK", $"勾选的策略: {string.Join(", ", enabledStrategies)}");
        WriteToLogFile("CHECK", $"realTimeStopPriceDiffRate > 0");
        WriteToLogFile("CHECK", $"remainingRisk > 0");

        // 对每个已勾选的策略分别进行判定
        foreach (var currentStrategy in enabledStrategies)
        {
            var higherStrategies = GetHigherStrategies(currentStrategy);
            var triggeredProducts = new List<ProductTriggerInfo>();

            WriteToLogFile("CHECK", $"");
            WriteToLogFile("CHECK", $"========== 判定策略: {currentStrategy} ==========");
            WriteToLogFile("CHECK", $"向上判定策略: {(higherStrategies.Count > 0 ? string.Join(", ", higherStrategies) : "无更高策略")}");

            // 第一步：列出当前策略满足条件的品种（direction != none 且 realTimeStopPriceDiffRate > 阈值）
            WriteToLogFile("CHECK", "");
            WriteToLogFile("CHECK", $"【第一步】当前策略 {currentStrategy} 满足条件的品种:");
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

                // 条件1: direction不能为none
                if (direction.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var realTimeStopPriceDiffRate = currentStrategyData["realTimeStopPriceDiffRate"]?.Value<double>() ?? 0;

                // 条件2: realTimeStopPriceDiffRate > 阈值
                if (checkRealTimeStop && realTimeStopPriceDiffRate <= realTimeStopThreshold)
                {
                    continue;
                }

                var remainingRisk = currentStrategyData["remainingRisk"]?.Value<double>() ?? 0;
                candidateProducts.Add((productId, direction, realTimeStopPriceDiffRate, remainingRisk, items));
                WriteToLogFile("CHECK", $"  {productId}: direction={direction}, realTimeStop={realTimeStopPriceDiffRate * 100:F2}%");
            }

            WriteToLogFile("CHECK", $"符合条件的品种数: {candidateProducts.Count}");

            if (candidateProducts.Count == 0)
            {
                WriteToLogFile("CHECK", "无符合条件的品种，跳过向上判定");
                continue;
            }

            // 第二步：对候选品种逐个向上判定
            WriteToLogFile("CHECK", "");
            WriteToLogFile("CHECK", $"【第二步】向上判定（方向一致性）:");

            int passCount = 0;
            foreach (var candidate in candidateProducts)
            {
                WriteToLogFile("CHECK", $"  {candidate.productId} ({candidate.direction}):");

                bool allMatch = true;
                var higherDirections = new Dictionary<string, string>();

                foreach (var higherStrategy in higherStrategies)
                {
                    var higherStrategyData = candidate.items[higherStrategy] as JObject;
                    
                    // 如果更高策略没有数据，直接判定失败
                    if (higherStrategyData == null)
                    {
                        WriteToLogFile("CHECK", $"    {higherStrategy}: 无数据");
                        WriteToLogFile("CHECK", $"      -> 无数据，判定失败");
                        allMatch = false;
                        break;
                    }
                    
                    var higherDir = higherStrategyData["direction"]?.ToString() ?? "";
                    
                    // 如果方向为空，判定失败
                    if (string.IsNullOrEmpty(higherDir) || higherDir.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteToLogFile("CHECK", $"    {higherStrategy}: direction={higherDir}");
                        WriteToLogFile("CHECK", $"      -> 方向为空，判定失败");
                        allMatch = false;
                        break;
                    }
                    
                    bool dirMatch = higherDir.Equals(candidate.direction, StringComparison.OrdinalIgnoreCase);
                    WriteToLogFile("CHECK", $"    {higherStrategy}: direction={higherDir}, 方向匹配:{dirMatch}");

                    if (!dirMatch)
                    {
                        WriteToLogFile("CHECK", $"      -> 方向不一致，判定失败");
                        allMatch = false;
                        break;
                    }

                    higherDirections[higherStrategy] = higherDir;
                }

                if (allMatch)
                {
                    passCount++;
                    WriteToLogFile("CHECK", $"    -> 全部满足，加入结果");

                    // 获取最高策略的 remainingRisk
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
                else
                {
                    WriteToLogFile("CHECK", $"    -> 判定失败");
                }
            }

            WriteToLogFile("CHECK", $"向上判定通过数: {passCount}/{candidateProducts.Count}");

            // 第三步：如果启用了剩余风险检查，过滤品种（remainingRisk > 0 才保留）
            if (checkRemainingRisk && triggeredProducts.Count > 0)
            {
                WriteToLogFile("CHECK", "");
                WriteToLogFile("CHECK", $"【第三步】剩余风险检查（remainingRisk > 0）:");
                
                var filteredProducts = triggeredProducts.Where(p => p.RemainingRisk > 0).ToList();
                WriteToLogFile("CHECK", $"  通过数: {filteredProducts.Count}/{triggeredProducts.Count}");
                
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

        WriteToLogFile("CHECK", $"========== 策略判定完成 ==========");
        WriteToLogFile("CHECK", $"满足条件的策略数: {results.Count}, 总品种数: {results.Sum(r => r.Products.Count)}");

        return results;
    }

    private async Task PushNotificationAsync(List<StrategyTriggerResult> results, JToken rawData)
    {
        if (_currentConfig == null) return;

        try
        {
            // 根据配置决定推送方式
            if (_currentConfig.EnableText)
            {
                await PushTextMessageAsync(results);
            }
        }
        catch (Exception ex)
        {
            _safeLogCallback("ERROR", $"推送通知失败: {ex.Message}");
            WriteToLogFile("ERROR", $"推送通知失败: {ex.Message}");
        }
    }

    private async Task PushTextMessageAsync(List<StrategyTriggerResult> results)
    {
        // 构造消息内容
        var messageContent = BuildMessageContent(results);
        
        _safeLogCallback("INFO", $"准备推送文本消息，长度: {messageContent.Length}");
        WriteToLogFile("INFO", $"推送内容:\n{messageContent}");

        // 将消息插入数据库，等待 FeishuPushService 推送
        var feishuMessage = new FeishuMessage
        {
            MessageType = "GD_Signal",
            TerminalId = _currentConfig.TerminalId,
            Method = "text",
            Status = "Pending",
            Body = messageContent,
            ReceivedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            SourceId = null  // GD 信号不使用外部消息源
        };

        var messageId = _databaseService.CreateMessage(feishuMessage);
        _safeLogCallback("INFO", $"消息已插入数据库，ID: {messageId}");
        WriteToLogFile("INFO", $"消息已插入数据库，ID: {messageId}");

        await Task.CompletedTask;
    }

    private string BuildMessageContent(List<StrategyTriggerResult> results)
    {
        // 使用配置的模板或默认模板
        var template = _currentConfig?.TextMessageTemplate;
        
        if (string.IsNullOrWhiteSpace(template))
        {
            return BuildDefaultMessageContent(results);
        }

        // 解析模板并替换变量
        var content = template;
        
        // 替换全局变量
        content = content.Replace("{Time}", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        
        // 收集所有策略和品种信息
        var allProducts = new List<string>();
        var strategyInfo = new StringBuilder();
        
        foreach (var result in results)
        {
            foreach (var product in result.Products)
            {
                var productLine = template
                    .Replace("{StrategyName}", result.StrategyName)
                    .Replace("{Direction}", product.CurrentDirection)
                    .Replace("{ProductId}", product.ProductId)
                    .Replace("{RealTimeStop}", product.RealTimeStopPriceDiffRate.ToString("P2"))
                    .Replace("{RemainingRisk}", product.RemainingRisk.ToString("P2"));
                
                allProducts.Add(productLine);
            }
            strategyInfo.AppendLine($"【{result.StrategyName}】方向: {result.Products.First().CurrentDirection}");
        }
        
        // 替换集合变量
        content = content.Replace("{Products}", string.Join("\n", allProducts));
        content = content.Replace("{StrategyInfo}", strategyInfo.ToString());
        
        return content;
    }

    private string BuildDefaultMessageContent(List<StrategyTriggerResult> results)
    {
        var messageBuilder = new StringBuilder();

        foreach (var result in results)
        {
            messageBuilder.AppendLine($"【{result.StrategyName}】触发提醒条件");

            messageBuilder.AppendLine("品种列表:");
            foreach (var product in result.Products.OrderBy(p => p.ProductId))
            {
                messageBuilder.AppendLine($"  {product.ProductId}");
                messageBuilder.AppendLine($"    实时止损价差: {product.RealTimeStopPriceDiffRate:P2}");
                messageBuilder.AppendLine($"    剩余风险: {product.RemainingRisk:P2}");
                messageBuilder.AppendLine($"    方向: {product.CurrentDirection}");
            }
            messageBuilder.AppendLine();
        }

        return messageBuilder.ToString();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isDisposing = true;
        StopMonitorTimer();
        
        // 关闭窗口时自动保存当前配置
        if (_currentConfig != null)
        {
            SaveConfigFromUI();
            _databaseService.SaveGDSignalConfig(_currentConfig);
        }
        
        base.OnClosed(e);
    }

    public void StartMonitoring()
    {
        _safeLogCallback("INFO", "准备启动监控...");

        // 先保存配置
        SaveConfigFromUI();
        _databaseService.SaveGDSignalConfig(_currentConfig!);

        _isMonitoring = true;
        _safeLogCallback("INFO", "GD策略监控已启动，_isMonitoring=true");

        // 更新 UI
        UpdateUIState();

        // 启动监控定时器
        StartMonitorTimer();
        
        _safeLogCallback("INFO", $"StartMonitoring 完成，定时器状态: {_monitorTimer != null}");
    }

    public void StopMonitoring()
    {
        _isMonitoring = false;
        _safeLogCallback("INFO", "GD策略监控已停止");

        // 更新 UI
        UpdateUIState();

        StopMonitorTimer();
    }
}

public class ConditionItem
{
    public string Field { get; set; } = "";
    public string Operator { get; set; } = "<";
    public double Value { get; set; }
    public string LogicOperator { get; set; } = "and";
}

public class TriggeredItem
{
    public string ProductId { get; set; } = "";
    public string StrategyName { get; set; } = "";
    public string Direction { get; set; } = "";
    public string TickTime { get; set; } = "";
    public double StopPriceDiffRate { get; set; }
    public double RealTimeStopPriceDiffRate { get; set; }
    public double RateProfitAndLoss { get; set; }
    public double ProfitAndLoss { get; set; }
    public double LastPrice { get; set; }
    public double OpenPrice { get; set; }
    public double OutPrice { get; set; }
}
