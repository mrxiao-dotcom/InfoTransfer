namespace InfoTransfer.Models;

public class FeishuMessage
{
    public int Id { get; set; }
    public string MessageType { get; set; } = "push";
    public string TerminalId { get; set; } = "";
    public string Method { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string Body { get; set; } = "{}";
    public string ReceivedAt { get; set; } = "";
    public string? ProcessedAt { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? SourceId { get; set; }

    public string DisplayId => $"#{Id}";
    public string DisplayTerminal => $"终端: {TerminalId}";
    public string DisplayTime => ReceivedAt.Length > 19 ? ReceivedAt.Substring(0, 19) : ReceivedAt;
    public string DisplayType => Method.ToLower() == "text" ? "文本" : "图片";
    public string DisplaySource => string.IsNullOrEmpty(SourceId) ? "" : $"来源: {SourceId}";
    public string DisplayStatus
    {
        get
        {
            return Status switch
            {
                "Pending" => "待处理",
                "Sent" => "已发送",
                "Failed" => "失败",
                _ => Status
            };
        }
    }
}

public class MessageRequest
{
    public string TerminalId { get; set; } = "";
    public string MessageType { get; set; } = "push";
    public string Method { get; set; } = "";
    // 支持数字或字符串形式的 SourceId
    public object? SourceId { get; set; }

    // 兼容旧的 SourceName 字段
    public string? SourceName { get; set; }

    // 获取最终的消息源ID（优先使用 SourceId，否则用 SourceName）
    public string GetSourceId()
    {
        if (SourceId != null)
        {
            if (SourceId is System.Text.Json.JsonElement jsonElement)
            {
                return jsonElement.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? jsonElement.GetInt32().ToString()
                    : jsonElement.GetString() ?? "";
            }
            return SourceId.ToString() ?? "";
        }
        return SourceName ?? "";
    }
}

public class ApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string Message { get; set; } = "";
}

public class FeishuTokenResponse
{
    public string? Code { get; set; }
    public string? Msg { get; set; }
    public string? TenantAccessToken { get; set; }
}

public class FeishuUploadResponse
{
    public string? Code { get; set; }
    public string? Msg { get; set; }
    public FeishuUploadData? Data { get; set; }
}

public class FeishuUploadData
{
    public string? ImageKey { get; set; }
}

public class FeishuSendResponse
{
    public string? Code { get; set; }
    public string? Msg { get; set; }
}
