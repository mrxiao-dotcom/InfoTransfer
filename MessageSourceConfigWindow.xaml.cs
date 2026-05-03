using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using InfoTransfer.Models;
using InfoTransfer.Services;

namespace InfoTransfer;

public partial class MessageSourceConfigWindow : Window
{
    private readonly DatabaseService _databaseService;
    private readonly ObservableCollection<MessageSource> _sources;
    private MessageSource? _currentSource;
    private bool _isAddingNew;
    private bool _isEditing;

    public MessageSourceConfigWindow(DatabaseService databaseService)
    {
        InitializeComponent();

        _databaseService = databaseService;
        _sources = new ObservableCollection<MessageSource>();

        LoadSources();
        UpdateUIState();
    }

    private void LoadSources()
    {
        _sources.Clear();
        foreach (var source in _databaseService.GetAllMessageSources())
        {
            _sources.Add(source);
        }
        SourceList.ItemsSource = _sources;
    }

    private void UpdateUIState()
    {
        bool hasSelection = _currentSource != null;

        BtnEdit.IsEnabled = hasSelection && !_isEditing && !_isAddingNew;
        BtnDelete.IsEnabled = hasSelection && !_isEditing && !_isAddingNew;

        bool showEditPanel = _isEditing || _isAddingNew;
        EditButtonGroup.Visibility = showEditPanel ? Visibility.Visible : Visibility.Collapsed;
        TxtEmptyHint.Visibility = showEditPanel ? Visibility.Collapsed : Visibility.Visible;

        if (_isAddingNew)
        {
            TxtStatus.Text = "正在添加新消息源...";
            TxtCurrentSource.Text = "（新建消息源）";
            TxtEmptyHint.Text = "请输入消息源信息后点击「保存」";
            EnableEditFields(true, true);
        }
        else if (_isEditing && _currentSource != null)
        {
            TxtStatus.Text = $"正在编辑: {_currentSource.SourceId}";
            TxtCurrentSource.Text = _currentSource.SourceId;
            TxtEmptyHint.Text = "请修改配置后点击「保存」";
            EnableEditFields(true, false);
        }
        else if (hasSelection)
        {
            TxtStatus.Text = $"已选择: {_currentSource?.SourceId}";
            TxtCurrentSource.Text = _currentSource?.SourceId;
            TxtEmptyHint.Text = "点击「修改」编辑此消息源，或点击「删除」删除";
            EnableEditFields(false, false);
        }
        else
        {
            TxtStatus.Text = "请选择或添加消息源";
            TxtCurrentSource.Text = "";
            TxtEmptyHint.Text = "请从左侧列表选择消息源，或点击「添加」创建新消息源";
            EnableEditFields(false, false);
        }
    }

    private void EnableEditFields(bool enabled, bool sourceIdEnabled)
    {
        TxtSourceId.IsEnabled = sourceIdEnabled;
        TxtName.IsEnabled = enabled;
        CmbApiMethod.IsEnabled = enabled;
        TxtApiUrl.IsEnabled = enabled;
        TxtApiParameters.IsEnabled = enabled;
        TxtApiToken.IsEnabled = enabled;
        TxtResponseFormat.IsEnabled = enabled;
        TxtDescription.IsEnabled = enabled;
    }

    private void ClearEditFields()
    {
        TxtSourceId.Text = "";
        TxtName.Text = "";
        CmbApiMethod.SelectedIndex = 0;
        TxtApiUrl.Text = "";
        TxtApiParameters.Text = "";
        TxtApiToken.Text = "";
        TxtResponseFormat.Text = "";
        TxtDescription.Text = "";
    }

    private void LoadSourceToEdit(MessageSource source)
    {
        TxtSourceId.Text = source.SourceId;
        TxtName.Text = source.Name;
        CmbApiMethod.SelectedItem = CmbApiMethod.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(x => x.Content?.ToString() == source.ApiMethod) ?? CmbApiMethod.Items[0];
        TxtApiUrl.Text = source.ApiUrl;
        TxtApiParameters.Text = source.ApiParameters;
        TxtApiToken.Text = source.ApiToken;
        TxtResponseFormat.Text = source.ResponseFormat;
        TxtDescription.Text = source.Description;
    }

    private void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isEditing || _isAddingNew)
        {
            SourceList.SelectedItem = _currentSource;
            return;
        }

        if (SourceList.SelectedItem is MessageSource selected)
        {
            _currentSource = selected;
            LoadSourceToEdit(selected);
        }
        else
        {
            _currentSource = null;
            ClearEditFields();
        }

        UpdateUIState();
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        _isAddingNew = true;
        _isEditing = false;
        _currentSource = null;
        ClearEditFields();
        EnableEditFields(true, true);
        UpdateUIState();
        TxtSourceId.Focus();
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSource == null)
        {
            MessageBox.Show("请先选择一个消息源", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isEditing = true;
        _isAddingNew = false;
        LoadSourceToEdit(_currentSource);
        UpdateUIState();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSource == null)
        {
            MessageBox.Show("请先选择一个消息源", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            $"确定要删除消息源 [{_currentSource.SourceId}] 吗?",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            _databaseService.DeleteMessageSource(_currentSource.Id);
            _sources.Remove(_currentSource);
            _currentSource = null;
            ClearEditFields();
            UpdateUIState();
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var sourceId = TxtSourceId.Text.Trim();

        if (string.IsNullOrWhiteSpace(sourceId))
        {
            MessageBox.Show("请输入 Source ID", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtSourceId.Focus();
            return;
        }

        var apiMethod = (CmbApiMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "GET";

        var source = new MessageSource
        {
            SourceId = sourceId,
            Name = TxtName.Text.Trim(),
            ApiMethod = apiMethod,
            ApiUrl = TxtApiUrl.Text.Trim(),
            ApiParameters = TxtApiParameters.Text.Trim(),
            ApiToken = TxtApiToken.Text.Trim(),
            ResponseFormat = TxtResponseFormat.Text.Trim(),
            Description = TxtDescription.Text.Trim()
        };

        if (_isAddingNew)
        {
            if (_sources.Any(s => s.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("Source ID 已存在", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtSourceId.Focus();
                return;
            }

            _databaseService.SaveMessageSource(source);
            LoadSources();
            _currentSource = _sources.FirstOrDefault(s => s.SourceId == sourceId);
            SourceList.SelectedItem = _currentSource;
        }
        else if (_isEditing && _currentSource != null)
        {
            source.Id = _currentSource.Id;
            _databaseService.SaveMessageSource(source);
            LoadSources();
            _currentSource = _sources.FirstOrDefault(s => s.SourceId == sourceId);
            SourceList.SelectedItem = _currentSource;
        }

        _isAddingNew = false;
        _isEditing = false;
        UpdateUIState();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _isAddingNew = false;
        _isEditing = false;

        if (_currentSource != null)
        {
            LoadSourceToEdit(_currentSource);
        }
        else
        {
            ClearEditFields();
        }

        UpdateUIState();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
