using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using InfoTransfer.Models;
using InfoTransfer.Services;

namespace InfoTransfer;

public partial class PushConfigWindow : Window
{
    private readonly DatabaseService _databaseService;
    private readonly ObservableCollection<TerminalConfig> _accounts;
    private readonly ObservableCollection<MessageSource> _sources;
    private readonly ObservableCollection<PushCombinationDisplay> _combinations;
    private TerminalConfig? _selectedAccount;
    private MessageSource? _selectedSource;
    private PushCombinationDisplay? _selectedCombination;

    public PushConfigWindow(DatabaseService databaseService)
    {
        InitializeComponent();

        _databaseService = databaseService;
        _accounts = new ObservableCollection<TerminalConfig>();
        _sources = new ObservableCollection<MessageSource>();
        _combinations = new ObservableCollection<PushCombinationDisplay>();

        LoadData();
        UpdateUIState();
    }

    private void LoadData()
    {
        // 从飞书推送配置（终端）加载账号
        _accounts.Clear();
        var terminalConfigs = _databaseService.GetAllTerminalConfigs();
        foreach (var config in terminalConfigs)
        {
            _accounts.Add(config);
        }
        AccountList.ItemsSource = _accounts;

        // 从消息源设置加载消息源
        _sources.Clear();
        var messageSources = _databaseService.GetAllMessageSources();
        foreach (var source in messageSources)
        {
            _sources.Add(source);
        }
        SourceList.ItemsSource = _sources;

        LoadCombinations();
    }

    private void LoadCombinations()
    {
        _combinations.Clear();
        var combos = _databaseService.GetAllPushCombinations();
        foreach (var combo in combos)
        {
            var account = _accounts.FirstOrDefault(a => a.TerminalId == combo.TerminalId);
            var source = _sources.FirstOrDefault(s => s.SourceId == combo.SourceId);

            var display = new PushCombinationDisplay
            {
                Id = combo.Id,
                TerminalId = combo.TerminalId,
                SourceId = combo.SourceId,
                EnableText = combo.EnableText,
                EnableImage = combo.EnableImage,
                AccountName = account?.TerminalId ?? combo.TerminalId,
                SourceName = source?.DisplayName ?? combo.SourceId
            };
            _combinations.Add(display);
        }
        CombinationList.ItemsSource = _combinations;
    }

    private void UpdateUIState()
    {
        bool canCreate = _selectedAccount != null && _selectedSource != null;
        BtnCreateCombination.IsEnabled = canCreate;

        ChkEnableText.IsChecked = false;
        ChkEnableImage.IsChecked = false;
    }

    private void AccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AccountList.SelectedItem is TerminalConfig selected)
        {
            _selectedAccount = selected;
        }
        else
        {
            _selectedAccount = null;
        }
        UpdateUIState();
    }

    private void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceList.SelectedItem is MessageSource selected)
        {
            _selectedSource = selected;
        }
        else
        {
            _selectedSource = null;
        }
        UpdateUIState();
    }

    private void CombinationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CombinationList.SelectedItem is PushCombinationDisplay selected)
        {
            _selectedCombination = selected;
            ChkEnableText.IsChecked = selected.EnableText;
            ChkEnableImage.IsChecked = selected.EnableImage;

            _selectedAccount = _accounts.FirstOrDefault(a => a.TerminalId == selected.TerminalId);
            _selectedSource = _sources.FirstOrDefault(s => s.SourceId == selected.SourceId);

            AccountList.SelectedItem = _selectedAccount;
            SourceList.SelectedItem = _selectedSource;
        }
        else
        {
            _selectedCombination = null;
        }
    }

    private void BtnCreateCombination_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAccount == null || _selectedSource == null)
        {
            MessageBox.Show("请先选择一个账号和一个消息源", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var enableText = ChkEnableText.IsChecked == true;
        var enableImage = ChkEnableImage.IsChecked == true;

        if (!enableText && !enableImage)
        {
            MessageBox.Show("请至少选择一种推送类型（文本或图片）", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var existingCombo = _combinations.FirstOrDefault(c =>
            c.TerminalId == _selectedAccount!.TerminalId && c.SourceId == _selectedSource!.SourceId);

        if (existingCombo != null)
        {
            existingCombo.EnableText = enableText;
            existingCombo.EnableImage = enableImage;
            CombinationList.Items.Refresh();

            var combo = new PushCombination
            {
                TerminalId = _selectedAccount.TerminalId,
                SourceId = _selectedSource.SourceId,
                EnableText = enableText,
                EnableImage = enableImage
            };
            _databaseService.SavePushCombination(combo);
            MessageBox.Show("组合已更新", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            var newCombo = new PushCombination
            {
                TerminalId = _selectedAccount!.TerminalId,
                SourceId = _selectedSource!.SourceId,
                EnableText = enableText,
                EnableImage = enableImage
            };
            _databaseService.SavePushCombination(newCombo);
            LoadCombinations();
            MessageBox.Show("组合已创建", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnDeleteCombination_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int comboId)
        {
            var result = MessageBox.Show(
                "确定要删除这个推送组合吗?",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _databaseService.DeletePushCombination(comboId);
                LoadCombinations();
            }
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

public class PushCombinationDisplay
{
    public int Id { get; set; }
    public string TerminalId { get; set; } = "";
    public string SourceId { get; set; } = "";
    public bool EnableText { get; set; }
    public bool EnableImage { get; set; }
    public string AccountName { get; set; } = "";
    public string SourceName { get; set; } = "";

    public string DisplayName => $"{AccountName} → {SourceName}";
}
