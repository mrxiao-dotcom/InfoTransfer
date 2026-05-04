using System;
using System.Collections.Generic;
using System.Linq;

namespace InfoTransfer.Models;

public class ActivityItem
{
    public string Type { get; set; } = "";
    public string TerminalId { get; set; } = "";
    public string? SourceId { get; set; }
    public string Method { get; set; } = "";
    public string Message { get; set; } = "";
    public string Time { get; set; } = "";
    public DateTime TimeValue { get; set; }
}

public class MessageSource
{
    public int Id { get; set; }
    public string SourceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ApiMethod { get; set; } = "GET";
    public string ApiUrl { get; set; } = "";
    public string ApiParameters { get; set; } = "";
    public string ResponseFormat { get; set; } = "";
    public string Description { get; set; } = "";
    public string ApiToken { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public string DisplayName => string.IsNullOrEmpty(Name) ? SourceId : Name;
}

public class PushAccount
{
    public int Id { get; set; }
    public string AccountId { get; set; } = "";
    public string Name { get; set; } = "";
    public string AccountType { get; set; } = "Feishu";
    public string Credentials { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public string DisplayName => string.IsNullOrEmpty(Name) ? AccountId : Name;
}

public class PushCombination
{
    public int Id { get; set; }
    public string TerminalId { get; set; } = "";
    public string SourceId { get; set; } = "";
    public bool EnableText { get; set; } = true;
    public bool EnableImage { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string DisplayName => $"组合 {Id}";
}

public class ServerConfig
{
    public int Port { get; set; } = 5000;
}

public class FeishuPushConfig
{
    public int ScanIntervalSeconds { get; set; } = 10;
    public List<TerminalConfig> Configs { get; set; } = new();
}

public class TerminalConfig
{
    public int Id { get; set; }
    public string TerminalId { get; set; } = "";
    public string TextWebhook { get; set; } = "";
    public string ImageApiKey { get; set; } = "";
    public string ImageSecretKey { get; set; } = "";
    public string ImageReceiverId { get; set; } = "";

    public string Description
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(TextWebhook)) parts.Add("文本");
            if (!string.IsNullOrWhiteSpace(ImageApiKey)) parts.Add("图片");
            return parts.Count > 0 ? string.Join(" + ", parts) + " 消息" : "未配置";
        }
    }
}

public class AppConfig
{
    public ServerConfig ServerConfig { get; set; } = new();
    public FeishuPushConfig FeishuPushConfig { get; set; } = new();
}

public class ScheduledTask
{
    public int Id { get; set; }
    public string TaskId { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string TerminalId { get; set; } = "";
    public bool EnableText { get; set; } = true;
    public bool EnableImage { get; set; } = false;
    /// <summary>
    /// 执行时间点，格式为 HH:mm，多个时间点用逗号分隔，如 "08:00,20:00"
    /// </summary>
    public string ScheduleTimes { get; set; } = "08:00,20:00";
    /// <summary>
    /// 上次执行的具体日期（用于判断是否今天已执行）
    /// </summary>
    public DateTime? LastRunTime { get; set; }
    /// <summary>
    /// 今天已执行的时间点列表
    /// </summary>
    public string ExecutedToday { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public string DisplayName => string.IsNullOrEmpty(Name) ? TaskId : Name;

    /// <summary>
    /// 获取所有配置的时间点
    /// </summary>
    public List<TimeSpan> GetScheduleTimeSpans()
    {
        var times = new List<TimeSpan>();
        if (string.IsNullOrWhiteSpace(ScheduleTimes))
            return times;

        var parts = ScheduleTimes.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (TimeSpan.TryParse(trimmed, out var ts))
            {
                times.Add(ts);
            }
        }
        return times.OrderBy(t => t).ToList();
    }

    /// <summary>
    /// 检查今天是否已执行过指定时间点
    /// </summary>
    public bool HasExecutedToday(TimeSpan time)
    {
        var key = time.ToString(@"hh\:mm");
        return ExecutedToday?.Contains(key) == true;
    }

    /// <summary>
    /// 标记今天已执行的时间点
    /// </summary>
    public void MarkExecutedToday(TimeSpan time)
    {
        var key = time.ToString(@"hh\:mm");
        if (string.IsNullOrEmpty(ExecutedToday))
        {
            ExecutedToday = key;
        }
        else if (!ExecutedToday.Contains(key))
        {
            ExecutedToday += "," + key;
        }
    }

    /// <summary>
    /// 检查是否需要重置今天的执行记录（跨天）
    /// </summary>
    public bool NeedsReset()
    {
        if (LastRunTime == null)
            return true;

        var today = DateTime.Today;
        var lastRunDate = LastRunTime.Value.Date;
        return lastRunDate < today;
    }
}

public class TaskHistory
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public string TaskIdStr { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string? ErrorMessage { get; set; }
    public string? ResponseData { get; set; }
    public DateTime ExecutedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// GD策略监控配置
/// </summary>
public class GDSignalConfig
{
    public int Id { get; set; }
    public string Name { get; set; } = "GD信号监控";
    /// <summary>
    /// API基础地址（不含路径）
    /// </summary>
    public string ApiBaseUrl { get; set; } = "http://43.136.60.93:30090";
    /// <summary>
    /// API Token认证
    /// </summary>
    public string ApiToken { get; set; } = "";
    /// <summary>
    /// 监控的策略列表，逗号分隔，如 "GD15,GD20,GD30"
    /// </summary>
    public string Strategys { get; set; } = "GD15";
    /// <summary>
    /// 主要监控策略勾选状态
    /// </summary>
    public bool EnableGD15 { get; set; } = true;
    public bool EnableGD20 { get; set; } = true;
    public bool EnableGD25 { get; set; } = true;
    public bool EnableGD30 { get; set; } = true;
    public bool EnableGD35 { get; set; } = true;
    public bool EnableGD40 { get; set; } = true;
    /// <summary>
    /// 是否启用 realTimeStopPriceDiffRate > 0 条件
    /// </summary>
    public bool EnableRealTimeStopPriceDiffRateCondition { get; set; } = true;
    /// <summary>
    /// realTimeStopPriceDiffRate 阈值（大于此值）
    /// </summary>
    public double RealTimeStopPriceDiffRateValue { get; set; } = 0;
    /// <summary>
    /// 是否启用 remainingRisk <= 0 条件
    /// </summary>
    public bool EnableRemainingRiskCondition { get; set; } = true;
    /// <summary>
    /// remainingRisk 阈值（小于等于此值）
    /// </summary>
    public double RemainingRiskValue { get; set; } = 0;
    /// <summary>
    /// 监控时间段-开始时间，格式 HH:mm
    /// </summary>
    public string MonitorStartTime { get; set; } = "09:00";
    /// <summary>
    /// 监控时间段-结束时间，格式 HH:mm
    /// </summary>
    public string MonitorEndTime { get; set; } = "15:00";
    /// <summary>
    /// 是否监控夜盘
    /// </summary>
    public bool MonitorNightSession { get; set; } = false;
    /// <summary>
    /// 夜盘开始时间，格式 HH:mm
    /// </summary>
    public string NightSessionStartTime { get; set; } = "21:00";
    /// <summary>
    /// 夜盘结束时间，格式 HH:mm
    /// </summary>
    public string NightSessionEndTime { get; set; } = "02:30";
    /// <summary>
    /// 监控频率：每隔N分钟
    /// </summary>
    public int MonitorIntervalMinutes { get; set; } = 30;
    /// <summary>
    /// 是否使用固定时间点提醒
    /// </summary>
    public bool UseFixedTimePoints { get; set; } = true;
    /// <summary>
    /// 固定时间点分钟值，逗号分隔（如 "0,30" 表示每小时的0分和30分）
    /// </summary>
    public string FixedTimeMinutes { get; set; } = "0,15,30,45";
    /// <summary>
    /// 推送目标终端ID
    /// </summary>
    public string TerminalId { get; set; } = "";
    /// <summary>
    /// 是否启用文本消息推送
    /// </summary>
    public bool EnableText { get; set; } = true;
    /// <summary>
    /// 是否启用图片消息推送
    /// </summary>
    public bool EnableImage { get; set; } = false;
    /// <summary>
    /// 文本消息模板，支持变量: {StrategyName}, {Direction}, {Products}, {Time}
    /// </summary>
    public string TextMessageTemplate { get; set; } = "";
    /// <summary>
    /// 是否启用（监控任务是否运行）
    /// </summary>
    public bool IsEnabled { get; set; } = false;
    /// <summary>
    /// 触发条件列表（JSON格式存储）
    /// </summary>
    public string Conditions { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// GD策略监控条件
/// </summary>
public class GDSignalCondition
{
    /// <summary>
    /// 字段名，如 stopPriceDiffRate, realTimeStopPriceDiffRate
    /// </summary>
    public string Field { get; set; } = "";
    /// <summary>
    /// 比较操作符：<, >, <=, >=
    /// </summary>
    public string Operator { get; set; } = "<";
    /// <summary>
    /// 比较值
    /// </summary>
    public double Value { get; set; }
    /// <summary>
    /// 逻辑操作符（与下一个条件的连接）：and, or
    /// </summary>
    public string LogicOperator { get; set; } = "and";
}

/// <summary>
/// GD策略监控触发记录
/// </summary>
public class GDSignalTrigger
{
    public int Id { get; set; }
    public int ConfigId { get; set; }
    public string StrategyName { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Condition { get; set; } = "";
    public double TriggerValue { get; set; }
    public DateTime TriggeredAt { get; set; } = DateTime.Now;
    public bool Pushed { get; set; } = false;
}
