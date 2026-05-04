using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InfoTransfer.Services;
using InfoTransfer.Models;

namespace InfoTransfer;

public partial class MainWindow : Window
{
    private readonly DatabaseService _databaseService;
    private readonly ConfigService _configService;
    private readonly ApiServerService _apiServerService;
    private readonly FeishuPushService _pushService;
    private readonly DataPushService _dataPushService;
    private readonly ObservableCollection<ActivityItem> _activities;
    private System.Timers.Timer? _statusUpdateTimer;
    private System.Timers.Timer? _activityUpdateTimer;
    private DateTime _lastActivityCheck = DateTime.MinValue;
    private readonly int _maxLogLines = 1000;

    public MainWindow()
    {
        InitializeComponent();

        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var dbPath = Path.Combine(appDir, "data.db");
        var configPath = Path.Combine(appDir, "appsettings.json");

        _databaseService = new DatabaseService(dbPath);
        _configService = new ConfigService(configPath, _databaseService);
        _apiServerService = new ApiServerService(_databaseService);
        _pushService = new FeishuPushService(_databaseService, _configService);
        _dataPushService = new DataPushService(_databaseService);

        _activities = new ObservableCollection<ActivityItem>();
        ActivityListBox.ItemsSource = _activities;

        _apiServerService.OnLog += Service_OnLog;
        _apiServerService.OnStatusChanged += Service_OnStatusChanged;

        _pushService.OnLog += Service_OnLog;
        _pushService.OnStatusChanged += Service_OnStatusChanged;

        _dataPushService.OnLog += Service_OnLog;
        _dataPushService.OnStatusChanged += Service_OnStatusChanged;

        _statusUpdateTimer = new System.Timers.Timer(2000);
        _statusUpdateTimer.Elapsed += (s, e) => Dispatcher.Invoke(UpdateStatusDisplay);
        _statusUpdateTimer.Start();

        _activityUpdateTimer = new System.Timers.Timer(3000);
        _activityUpdateTimer.Elapsed += (s, e) => Dispatcher.Invoke(LoadRecentActivities);
        _activityUpdateTimer.Start();

        LoadConfigToUI();
        UpdateStatusDisplay();
        LoadRecentActivities();

        AddLog("INFO", "应用程序已启动");
    }

    private void LoadRecentActivities()
    {
        var recentMessages = _databaseService.GetRecentMessages(30);

        Dispatcher.Invoke(() =>
        {
            _activities.Clear();

            foreach (var msg in recentMessages)
            {
                DateTime msgTime;
                string statusText;

                if (!string.IsNullOrEmpty(msg.ProcessedAt) && DateTime.TryParse(msg.ProcessedAt, out var pt))
                {
                    msgTime = pt;
                    statusText = msg.Status switch
                    {
                        "Sent" => "推送成功",
                        "Failed" => "推送失败",
                        _ => "已处理"
                    };
                }
                else
                {
                    msgTime = DateTime.Parse(msg.ReceivedAt);
                    statusText = "待处理";
                }

                var activity = new ActivityItem
                {
                    Type = msg.Status switch
                    {
                        "Sent" => "Sent",
                        "Failed" => "Error",
                        _ => "Received"
                    },
                    TerminalId = msg.TerminalId,
                    SourceId = msg.SourceId,
                    Method = msg.Method.ToUpper(),
                    Message = $"{msg.Method.ToUpper()} - {statusText}",
                    Time = msgTime.ToString("HH:mm:ss"),
                    TimeValue = msgTime
                };

                _activities.Add(activity);
            }

            TxtActivityCount.Text = $"({_activities.Count})";
        });
    }

    private void LoadConfigToUI()
    {
        TxtServerUrl.Text = $"API地址: http://localhost:{_configService.Config.ServerConfig.Port}";
    }

    private void Service_OnLog(object? sender, Models.LogEntry e)
    {
        Dispatcher.Invoke(() => AddLog(e.Level, e.Message));
    }

    private void Service_OnStatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(UpdateStatusDisplay);
    }

    private void AddLog(string level, string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";

        LogTextBox.Text = entry + Environment.NewLine + LogTextBox.Text;

        // 限制日志行数
        var lines = LogTextBox.Text.Split('\n');
        if (lines.Length > _maxLogLines)
        {
            LogTextBox.Text = string.Join(Environment.NewLine, lines.Take(_maxLogLines));
        }
    }

    private void UpdateStatusDisplay()
    {
        var apiRunning = _apiServerService.IsRunning;
        var pushRunning = _pushService.IsRunning;

        BtnStartServer.IsEnabled = !apiRunning;
        BtnStopServer.IsEnabled = apiRunning;

        StatusIndicator.Fill = apiRunning || pushRunning
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#52C41A"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D9D9D9"));

        TxtStatus.Text = apiRunning || pushRunning ? "运行中" : "已停止";
        TxtApiStatus.Text = apiRunning ? $"运行中 (端口:{_apiServerService.Port})" : "未运行";
        TxtApiStatus.Foreground = apiRunning
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#52C41A"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#999"));

        TxtPushStatus.Text = pushRunning ? "运行中" : "未运行";
        TxtPushStatus.Foreground = pushRunning
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#52C41A"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#999"));

        var counts = _databaseService.GetMessageCounts();
        TxtPendingCount.Text = counts.Pending.ToString();
        TxtSentCount.Text = counts.Sent.ToString();
        TxtFailedCount.Text = counts.Failed.ToString();

        if (pushRunning)
        {
            TxtLastScan.Text = $"最后扫描: {DateTime.Now:HH:mm:ss}";
        }
    }

    private void BtnStartServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var port = _configService.Config.ServerConfig.Port;
            _apiServerService.Start(port);
            _pushService.Start();

            TxtServerUrl.Text = $"API地址: http://localhost:{port}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动服务失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnStopServer_Click(object sender, RoutedEventArgs e)
    {
        _apiServerService.Stop();
        _pushService.Stop();
    }

    private void BtnServerConfig_Click(object sender, RoutedEventArgs e)
    {
        var configWindow = new ServerConfigWindow(
            _databaseService,
            _configService,
            (msg) => AddLog("INFO", msg),
            SendTestMessageAsync);
        configWindow.Owner = this;
        configWindow.ShowDialog();
    }

    private async Task SendTestMessageAsync(int msgId, int terminalId, string targetTerminal, string method)
    {
        var now = DateTime.Now;
        var body = method == "text"
            ? "{\"content\":\"测试消息\"}"
            : "{\"image_key\":\"test_image_key\"}";

        var message = new FeishuMessage
        {
            MessageType = "push",
            TerminalId = targetTerminal,
            Method = method,
            Status = "Pending",
            Body = body,
            ReceivedAt = now.ToString("o"),
            CreatedAt = now.ToString("o")
        };

        _databaseService.CreateMessage(message);
    }

    private void BtnFeishuConfig_Click(object sender, RoutedEventArgs e)
    {
        var configWindow = new FeishuConfigWindow(_configService);
        configWindow.Owner = this;
        configWindow.ShowDialog();
    }

    private void BtnMessageSource_Click(object sender, RoutedEventArgs e)
    {
        var sourceWindow = new MessageSourceConfigWindow(_databaseService);
        sourceWindow.Owner = this;
        sourceWindow.ShowDialog();
    }

    private void BtnPushConfig_Click(object sender, RoutedEventArgs e)
    {
        var pushWindow = new PushConfigWindow(_databaseService);
        pushWindow.Owner = this;
        pushWindow.ShowDialog();
    }

    private void BtnScheduledTask_Click(object sender, RoutedEventArgs e)
    {
        var taskWindow = new ScheduledTaskConfigWindow(_databaseService);
        taskWindow.Owner = this;
        taskWindow.ShowDialog();
    }

    private void BtnGDSignalConfig_Click(object sender, RoutedEventArgs e)
    {
        var gdWindow = new GDSignalConfigWindow(_databaseService, _configService, AddLog);
        gdWindow.Owner = this;
        gdWindow.ShowDialog();
    }

    private void BtnGDSignalMonitor_Click(object sender, RoutedEventArgs e)
    {
        // 直接打开配置窗口并自动启动监控
        var gdWindow = new GDSignalConfigWindow(_databaseService, _configService, AddLog);
        gdWindow.Owner = this;
        
        // 在窗口加载完成后自动启动监控
        gdWindow.Loaded += (s, args) =>
        {
            gdWindow.StartMonitoring();
        };
        
        gdWindow.ShowDialog();
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Text = "";
    }

    private void BtnRefreshMessages_Click(object sender, RoutedEventArgs e)
    {
        LoadRecentActivities();
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusUpdateTimer?.Stop();
        _statusUpdateTimer?.Dispose();
        _activityUpdateTimer?.Stop();
        _activityUpdateTimer?.Dispose();

        _apiServerService?.Stop();
        _pushService?.Stop();

        base.OnClosed(e);
    }
}
