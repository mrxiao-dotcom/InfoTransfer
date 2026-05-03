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
