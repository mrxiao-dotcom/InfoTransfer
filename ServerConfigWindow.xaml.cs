using System;
using System.Threading.Tasks;
using System.Windows;
using InfoTransfer.Services;
using Microsoft.Win32;

namespace InfoTransfer;

public partial class ServerConfigWindow : Window
{
    private readonly DatabaseService _databaseService;
    private readonly ConfigService _configService;
    private readonly Action<string> _logAction;
    private readonly Func<int, int, string, string, Task> _testMessageAction;

    public ServerConfigWindow(
        DatabaseService databaseService,
        ConfigService configService,
        Action<string> logAction,
        Func<int, int, string, string, Task> testMessageAction)
    {
        InitializeComponent();

        _databaseService = databaseService;
        _configService = configService;
        _logAction = logAction;
        _testMessageAction = testMessageAction;

        LoadConfig();
    }

    private void LoadConfig()
    {
        TxtPort.Text = _configService.Config.ServerConfig.Port.ToString();
        TxtScanInterval.Text = _configService.Config.FeishuPushConfig.ScanIntervalSeconds.ToString();
    }

    private void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(TxtPort.Text, out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show("请输入有效的端口号（1-65535）", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtScanInterval.Text, out int interval) || interval < 1)
            {
                MessageBox.Show("请输入有效的扫描间隔（秒）", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _configService.Config.ServerConfig.Port = port;
            _configService.Config.FeishuPushConfig.ScanIntervalSeconds = interval;
            _configService.SaveConfig();

            _logAction($"配置已保存：端口={port}, 扫描间隔={interval}秒");
            MessageBox.Show("配置保存成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Filter = "数据库文件|*.db",
                FileName = $"backup_{DateTime.Now:yyyyMMdd_HHmmss}.db"
            };

            if (dialog.ShowDialog() == true)
            {
                _databaseService.RestoreFrom(dialog.FileName);
                _logAction($"数据库已备份到: {dialog.FileName}");
                MessageBox.Show($"备份成功!\n{dialog.FileName}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"备份失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Filter = "数据库文件|*.db"
            };

            if (dialog.ShowDialog() == true)
            {
                var result = MessageBox.Show("恢复操作将覆盖现有数据，是否继续？", "警告",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    _databaseService.RestoreFrom(dialog.FileName);
                    _logAction($"数据库已从备份恢复: {dialog.FileName}");
                    MessageBox.Show("恢复成功！请重启应用程序。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"恢复失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnClearMessages_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("确定要清空所有消息记录吗？此操作不可恢复！", "警告",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                _databaseService.ClearAllMessages();
                _logAction("消息记录已清空");
                MessageBox.Show("消息记录已清空", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"清空失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void BtnTestMessage_Click(object sender, RoutedEventArgs e)
    {
        var terminalId = TxtTestTerminalId.Text.Trim();
        if (string.IsNullOrEmpty(terminalId))
        {
            MessageBox.Show("请输入终端ID", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var method = RbText.IsChecked == true ? "text" : "image";
            await _testMessageAction(0, 0, terminalId, method);
            _logAction($"测试消息已发送: {method} -> {terminalId}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"发送失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
