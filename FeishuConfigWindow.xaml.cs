using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using InfoTransfer.Models;
using InfoTransfer.Services;

namespace InfoTransfer;

public partial class FeishuConfigWindow : Window
{
    private readonly ConfigService _configService;
    private readonly ObservableCollection<TerminalConfig> _terminalConfigs;
    private TerminalConfig? _currentTerminal;
    private bool _isAddingNew;
    private bool _isEditing;

    public FeishuConfigWindow(ConfigService configService)
    {
        InitializeComponent();

        _configService = configService;
        _terminalConfigs = new ObservableCollection<TerminalConfig>();

        LoadConfig();
        UpdateUIState();
    }

    private void LoadConfig()
    {
        TxtScanInterval.Text = _configService.Config.FeishuPushConfig.ScanIntervalSeconds.ToString();

        _terminalConfigs.Clear();
        foreach (var config in _configService.Config.FeishuPushConfig.Configs)
        {
            _terminalConfigs.Add(new TerminalConfig
            {
                Id = config.Id,
                TerminalId = config.TerminalId,
                TextWebhook = config.TextWebhook,
                ImageApiKey = config.ImageApiKey,
                ImageSecretKey = config.ImageSecretKey,
                ImageReceiverId = config.ImageReceiverId
            });
        }

        TerminalList.ItemsSource = _terminalConfigs;
    }

    private void UpdateUIState()
    {
        bool hasSelection = _currentTerminal != null;

        BtnEdit.IsEnabled = hasSelection && !_isEditing && !_isAddingNew;
        BtnDelete.IsEnabled = hasSelection && !_isEditing && !_isAddingNew;

        bool showEditPanel = _isEditing || _isAddingNew;
        EditButtonGroup.Visibility = showEditPanel ? Visibility.Visible : Visibility.Collapsed;
        TxtEmptyHint.Visibility = showEditPanel ? Visibility.Collapsed : Visibility.Visible;

        if (_isAddingNew)
        {
            TxtStatus.Text = "正在添加新终端...";
            TxtCurrentTerminal.Text = "（新建终端）";
            TxtEmptyHint.Text = "请输入终端信息后点击「保存」";
            EnableEditFields(true, true);
        }
        else if (_isEditing && _currentTerminal != null)
        {
            TxtStatus.Text = $"正在编辑: {_currentTerminal.TerminalId}";
            TxtCurrentTerminal.Text = _currentTerminal.TerminalId;
            TxtEmptyHint.Text = "请修改配置后点击「保存」";
            EnableEditFields(true, false);
        }
        else if (hasSelection)
        {
            TxtStatus.Text = $"已选择: {_currentTerminal?.TerminalId}";
            TxtCurrentTerminal.Text = _currentTerminal?.TerminalId;
            TxtEmptyHint.Text = "点击「修改」编辑此终端，或点击「删除」删除";
            EnableEditFields(false, false);
        }
        else
        {
            TxtStatus.Text = "请选择或添加终端";
            TxtCurrentTerminal.Text = "";
            TxtEmptyHint.Text = "请从左侧列表选择终端，或点击「添加」创建新终端";
            EnableEditFields(false, false);
        }
    }

    private void EnableEditFields(bool enabled, bool terminalIdEnabled)
    {
        TxtTerminalId.IsEnabled = terminalIdEnabled;
        TxtTextWebhook.IsEnabled = enabled;
        TxtImageApiKey.IsEnabled = enabled;
        TxtImageSecretKey.IsEnabled = enabled;
        TxtImageReceiverId.IsEnabled = enabled;
    }

    private void ClearEditFields()
    {
        TxtTerminalId.Text = "";
        TxtTextWebhook.Text = "";
        TxtImageApiKey.Text = "";
        TxtImageSecretKey.Text = "";
        TxtImageReceiverId.Text = "";
    }

    private void LoadTerminalToEdit(TerminalConfig config)
    {
        TxtTerminalId.Text = config.TerminalId;
        TxtTextWebhook.Text = config.TextWebhook;
        TxtImageApiKey.Text = config.ImageApiKey;
        TxtImageSecretKey.Text = config.ImageSecretKey;
        TxtImageReceiverId.Text = config.ImageReceiverId;
    }

    private void TerminalList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isEditing || _isAddingNew)
        {
            TerminalList.SelectedItem = _currentTerminal;
            return;
        }

        if (TerminalList.SelectedItem is TerminalConfig selected)
        {
            _currentTerminal = selected;
            LoadTerminalToEdit(selected);
        }
        else
        {
            _currentTerminal = null;
            ClearEditFields();
        }

        UpdateUIState();
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        _isAddingNew = true;
        _isEditing = false;
        _currentTerminal = null;
        ClearEditFields();
        EnableEditFields(true, true);
        UpdateUIState();
        TxtTerminalId.Focus();
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTerminal == null)
        {
            MessageBox.Show("请先选择一个终端", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isEditing = true;
        _isAddingNew = false;
        LoadTerminalToEdit(_currentTerminal);
        UpdateUIState();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTerminal == null)
        {
            MessageBox.Show("请先选择一个终端", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            $"确定要删除终端 [{_currentTerminal.TerminalId}] 吗?",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            var removeIndex = _terminalConfigs.IndexOf(_currentTerminal);
            _terminalConfigs.Remove(_currentTerminal);
            _currentTerminal = null;
            ClearEditFields();

            if (_terminalConfigs.Count > 0)
            {
                TerminalList.SelectedIndex = Math.Min(removeIndex, _terminalConfigs.Count - 1);
            }

            UpdateUIState();
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var terminalId = TxtTerminalId.Text.Trim();

        if (string.IsNullOrWhiteSpace(terminalId))
        {
            MessageBox.Show("请输入终端ID", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtTerminalId.Focus();
            return;
        }

        if (_isAddingNew)
        {
            if (_terminalConfigs.Any(t => t.TerminalId.Equals(terminalId, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("终端ID已存在", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtTerminalId.Focus();
                return;
            }

            var newConfig = new TerminalConfig
            {
                TerminalId = terminalId,
                TextWebhook = TxtTextWebhook.Text.Trim(),
                ImageApiKey = TxtImageApiKey.Text.Trim(),
                ImageSecretKey = TxtImageSecretKey.Text.Trim(),
                ImageReceiverId = TxtImageReceiverId.Text.Trim()
            };

            _terminalConfigs.Add(newConfig);
            _currentTerminal = newConfig;
            TerminalList.SelectedItem = newConfig;
        }
        else if (_isEditing && _currentTerminal != null)
        {
            _currentTerminal.TextWebhook = TxtTextWebhook.Text.Trim();
            _currentTerminal.ImageApiKey = TxtImageApiKey.Text.Trim();
            _currentTerminal.ImageSecretKey = TxtImageSecretKey.Text.Trim();
            _currentTerminal.ImageReceiverId = TxtImageReceiverId.Text.Trim();

            var index = _terminalConfigs.IndexOf(_currentTerminal);
            if (index >= 0)
            {
                _terminalConfigs[index] = _currentTerminal;
                TerminalList.Items.Refresh();
            }
        }

        _isAddingNew = false;
        _isEditing = false;
        UpdateUIState();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _isAddingNew = false;
        _isEditing = false;

        if (_currentTerminal != null)
        {
            LoadTerminalToEdit(_currentTerminal);
        }
        else
        {
            ClearEditFields();
        }

        UpdateUIState();
    }

    private void BtnSaveAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isAddingNew || _isEditing)
        {
            MessageBox.Show("请先完成当前的编辑操作（保存或取消）", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(TxtScanInterval.Text, out int scanInterval) || scanInterval < 1)
        {
            MessageBox.Show("请输入有效的扫描间隔（大于0）", "配置错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_terminalConfigs.Count == 0)
        {
            MessageBox.Show("请至少添加一个终端", "配置错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_terminalConfigs.Count != _terminalConfigs.Select(t => t.TerminalId).Distinct().Count())
        {
            MessageBox.Show("终端ID不能重复", "配置错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        foreach (var config in _terminalConfigs)
        {
            if (string.IsNullOrWhiteSpace(config.TerminalId))
            {
                MessageBox.Show("终端ID不能为空", "配置错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        _configService.Config.FeishuPushConfig.ScanIntervalSeconds = scanInterval;
        _configService.SaveTerminalConfigs(_terminalConfigs.ToList());

        MessageBox.Show("配置已保存到数据库", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
        Close();
    }
}
