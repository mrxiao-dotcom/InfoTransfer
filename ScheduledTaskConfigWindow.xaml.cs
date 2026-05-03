using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using InfoTransfer.Models;
using InfoTransfer.Services;

namespace InfoTransfer;

public partial class ScheduledTaskConfigWindow : Window
{
    private readonly DatabaseService _databaseService;
    private readonly ObservableCollection<ScheduledTask> _tasks;
    private readonly ObservableCollection<MessageSource> _sources;
    private readonly ObservableCollection<TerminalConfig> _terminals;
    private ScheduledTask? _currentTask;
    private bool _isAddingNew;
    private bool _isEditing;

    public ScheduledTaskConfigWindow(DatabaseService databaseService)
    {
        InitializeComponent();

        _databaseService = databaseService;
        _tasks = new ObservableCollection<ScheduledTask>();
        _sources = new ObservableCollection<MessageSource>();
        _terminals = new ObservableCollection<TerminalConfig>();

        LoadData();
        UpdateUIState();
    }

    private void LoadData()
    {
        _tasks.Clear();
        foreach (var task in _databaseService.GetAllScheduledTasks())
        {
            _tasks.Add(task);
        }
        TaskList.ItemsSource = _tasks;

        _sources.Clear();
        foreach (var source in _databaseService.GetAllMessageSources())
        {
            _sources.Add(source);
        }
        CmbSource.ItemsSource = _sources;

        _terminals.Clear();
        foreach (var terminal in _databaseService.GetAllTerminalConfigs())
        {
            _terminals.Add(terminal);
        }
        CmbTerminal.ItemsSource = _terminals;
    }

    private void UpdateUIState()
    {
        bool hasSelection = _currentTask != null;

        BtnEdit.IsEnabled = hasSelection && !_isEditing && !_isAddingNew;
        BtnDelete.IsEnabled = hasSelection && !_isEditing && !_isAddingNew;

        bool showEditPanel = _isEditing || _isAddingNew;
        EditButtonGroup.Visibility = showEditPanel ? Visibility.Visible : Visibility.Collapsed;
        TxtEmptyHint.Visibility = showEditPanel ? Visibility.Collapsed : Visibility.Visible;

        if (_isAddingNew)
        {
            TxtStatus.Text = "正在添加新任务...";
            TxtCurrentTask.Text = "（新建任务）";
            TxtEmptyHint.Text = "请选择消息源和终端后点击「保存」";
            EnableEditFields(true, true);
            LastRunPanel.Visibility = Visibility.Collapsed;
            TodayExecutedPanel.Visibility = Visibility.Collapsed;
        }
        else if (_isEditing && _currentTask != null)
        {
            TxtStatus.Text = $"正在编辑: {_currentTask.DisplayName}";
            TxtCurrentTask.Text = _currentTask.DisplayName;
            TxtEmptyHint.Text = "请修改配置后点击「保存」";
            EnableEditFields(true, false);
            LastRunPanel.Visibility = Visibility.Collapsed;
            TodayExecutedPanel.Visibility = Visibility.Collapsed;
        }
        else if (hasSelection)
        {
            TxtStatus.Text = $"已选择: {_currentTask?.DisplayName}";
            TxtCurrentTask.Text = _currentTask?.DisplayName;
            TxtEmptyHint.Text = "点击「修改」编辑此任务，或点击「删除」删除";
            EnableEditFields(false, false);
            LastRunPanel.Visibility = Visibility.Visible;
            TodayExecutedPanel.Visibility = Visibility.Visible;
        }
        else
        {
            TxtStatus.Text = "请选择或添加定时任务";
            TxtCurrentTask.Text = "";
            TxtEmptyHint.Text = "请从左侧列表选择任务，或点击「添加任务」创建新任务";
            EnableEditFields(false, false);
            LastRunPanel.Visibility = Visibility.Collapsed;
            TodayExecutedPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void EnableEditFields(bool enabled, bool taskIdEnabled)
    {
        TxtTaskId.IsEnabled = taskIdEnabled;
        TxtTaskName.IsEnabled = enabled;
        CmbSource.IsEnabled = enabled;
        CmbTerminal.IsEnabled = enabled;
        ChkEnableText.IsEnabled = enabled;
        ChkEnableImage.IsEnabled = enabled;
        TxtScheduleTimes.IsEnabled = enabled;
        ChkIsEnabled.IsEnabled = enabled;
    }

    private void ClearEditFields()
    {
        TxtTaskId.Text = "";
        TxtTaskName.Text = "";
        CmbSource.SelectedIndex = -1;
        CmbTerminal.SelectedIndex = -1;
        ChkEnableText.IsChecked = true;
        ChkEnableImage.IsChecked = false;
        TxtScheduleTimes.Text = "08:00,20:00";
        ChkIsEnabled.IsChecked = true;
        TxtLastRun.Text = "-";
        TxtTodayExecuted.Text = "-";
    }

    private void LoadTaskToEdit(ScheduledTask task)
    {
        TxtTaskId.Text = task.TaskId;
        TxtTaskName.Text = task.Name;

        var source = _sources.FirstOrDefault(s => s.SourceId == task.SourceId);
        CmbSource.SelectedItem = source;

        var terminal = _terminals.FirstOrDefault(t => t.TerminalId == task.TerminalId);
        CmbTerminal.SelectedItem = terminal;

        ChkEnableText.IsChecked = task.EnableText;
        ChkEnableImage.IsChecked = task.EnableImage;
        TxtScheduleTimes.Text = string.IsNullOrWhiteSpace(task.ScheduleTimes) ? "08:00,20:00" : task.ScheduleTimes;
        ChkIsEnabled.IsChecked = task.IsEnabled;

        // 显示上次执行信息
        if (task.LastRunTime.HasValue)
        {
            TxtLastRun.Text = task.LastRunTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
        }
        else
        {
            TxtLastRun.Text = "从未执行";
        }

        // 显示今日已执行
        if (!string.IsNullOrWhiteSpace(task.ExecutedToday))
        {
            var times = task.ExecutedToday.Split(',');
            TxtTodayExecuted.Text = string.Join(", ", times.Select(t => t.Trim()));
        }
        else
        {
            TxtTodayExecuted.Text = "无";
        }

        LastRunPanel.Visibility = Visibility.Visible;
        TodayExecutedPanel.Visibility = Visibility.Visible;
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isEditing || _isAddingNew)
        {
            TaskList.SelectedItem = _currentTask;
            return;
        }

        if (TaskList.SelectedItem is ScheduledTask selected)
        {
            _currentTask = selected;
            LoadTaskToEdit(selected);
        }
        else
        {
            _currentTask = null;
            ClearEditFields();
        }

        UpdateUIState();
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_sources.Count == 0)
        {
            MessageBox.Show("请先在「消息源设置」中添加消息源", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_terminals.Count == 0)
        {
            MessageBox.Show("请先在「飞书推送配置」中添加终端", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isAddingNew = true;
        _isEditing = false;
        _currentTask = null;
        ClearEditFields();

        TxtTaskId.Text = "task_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        EnableEditFields(true, false);
        UpdateUIState();
        TxtTaskName.Focus();
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTask == null)
        {
            MessageBox.Show("请先选择一个任务", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isEditing = true;
        _isAddingNew = false;
        LoadTaskToEdit(_currentTask);
        UpdateUIState();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTask == null)
        {
            MessageBox.Show("请先选择一个任务", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            $"确定要删除任务 [{_currentTask.DisplayName}] 吗?",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            _databaseService.DeleteScheduledTask(_currentTask.Id);
            _tasks.Remove(_currentTask);
            _currentTask = null;
            ClearEditFields();
            UpdateUIState();
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var taskId = TxtTaskId.Text.Trim();
        if (string.IsNullOrWhiteSpace(taskId))
        {
            MessageBox.Show("Task ID 不能为空", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var name = TxtTaskName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = taskId;
        }

        var selectedSource = CmbSource.SelectedItem as MessageSource;
        if (selectedSource == null)
        {
            MessageBox.Show("请选择消息源", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selectedTerminal = CmbTerminal.SelectedItem as TerminalConfig;
        if (selectedTerminal == null)
        {
            MessageBox.Show("请选择推送终端", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var scheduleTimes = TxtScheduleTimes.Text.Trim();
        if (string.IsNullOrWhiteSpace(scheduleTimes))
        {
            MessageBox.Show("请输入执行时间", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtScheduleTimes.Focus();
            return;
        }

        // 验证时间格式
        var parts = scheduleTimes.Split(',', StringSplitOptions.RemoveEmptyEntries);
        bool hasValidTime = false;
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (TimeSpan.TryParse(trimmed, out _))
            {
                hasValidTime = true;
            }
            else
            {
                MessageBox.Show($"时间格式错误: {trimmed}，正确格式如 08:00", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtScheduleTimes.Focus();
                return;
            }
        }

        if (!hasValidTime)
        {
            MessageBox.Show("请至少输入一个有效的时间点", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtScheduleTimes.Focus();
            return;
        }

        if (!ChkEnableText.IsChecked == true && !ChkEnableImage.IsChecked == true)
        {
            MessageBox.Show("请至少选择一种推送类型（文字或图片）", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var task = new ScheduledTask
        {
            TaskId = taskId,
            Name = name,
            SourceId = selectedSource.SourceId,
            TerminalId = selectedTerminal.TerminalId,
            EnableText = ChkEnableText.IsChecked == true,
            EnableImage = ChkEnableImage.IsChecked == true,
            ScheduleTimes = scheduleTimes,
            IsEnabled = ChkIsEnabled.IsChecked == true
        };

        if (_isAddingNew)
        {
            if (_tasks.Any(t => t.TaskId == taskId))
            {
                MessageBox.Show("Task ID 已存在", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _databaseService.SaveScheduledTask(task);
            LoadData();
            _currentTask = _tasks.FirstOrDefault(t => t.TaskId == taskId);
            TaskList.SelectedItem = _currentTask;
        }
        else if (_isEditing && _currentTask != null)
        {
            task.Id = _currentTask.Id;
            task.LastRunTime = _currentTask.LastRunTime;
            task.ExecutedToday = _currentTask.ExecutedToday;
            task.CreatedAt = _currentTask.CreatedAt;
            _databaseService.SaveScheduledTask(task);
            LoadData();
            _currentTask = _tasks.FirstOrDefault(t => t.TaskId == taskId);
            TaskList.SelectedItem = _currentTask;
        }

        _isAddingNew = false;
        _isEditing = false;
        UpdateUIState();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _isAddingNew = false;
        _isEditing = false;

        if (_currentTask != null)
        {
            LoadTaskToEdit(_currentTask);
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
