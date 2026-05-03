using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using InfoTransfer.Models;

namespace InfoTransfer.Services;

public class FeishuPushService : IDisposable
{
    private readonly DatabaseService _databaseService;
    private readonly ConfigService _configService;
    private readonly HttpClient _httpClient;
    private readonly MessageSourceService _messageSourceService;
    private readonly ImageGeneratorService _imageGenerator;
    private System.Threading.Timer? _scanTimer;
    private bool _isRunning;

    public event EventHandler<LogEntry>? OnLog;
    public event EventHandler? OnStatusChanged;

    public bool IsRunning => _isRunning;

    public FeishuPushService(DatabaseService databaseService, ConfigService configService)
    {
        _databaseService = databaseService;
        _configService = configService;
        _httpClient = new HttpClient();
        _messageSourceService = new MessageSourceService(databaseService);
        _messageSourceService.OnLog += (s, entry) => OnLog?.Invoke(s, entry);
        _imageGenerator = new ImageGeneratorService();
    }

    public void Start()
    {
        if (_isRunning) return;

        var config = _configService.Config.FeishuPushConfig;
        var intervalMs = config.ScanIntervalSeconds * 1000;

        _scanTimer = new Timer(ScanCallback, null, 0, intervalMs);
        _isRunning = true;

        Log("INFO", $"飞书推送服务已启动，扫描间隔: {config.ScanIntervalSeconds}秒");
        OnStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _scanTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _scanTimer?.Dispose();
        _scanTimer = null;
        _isRunning = false;

        Log("INFO", "飞书推送服务已停止");
        OnStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateInterval(int seconds)
    {
        if (_isRunning && _scanTimer != null)
        {
            _scanTimer.Change(0, seconds * 1000);
            Log("INFO", $"扫描间隔已更新为: {seconds}秒");
        }
    }

    private void ScanCallback(object? state)
    {
        try
        {
            ScanAndProcess();
        }
        catch (Exception ex)
        {
            Log("ERROR", $"扫描异常: {ex.Message}");
        }
    }

    private async void ScanAndProcess()
    {
        var pendingMessages = _databaseService.GetMessagesByStatus("Pending", 50);

        if (pendingMessages.Count == 0)
            return;

        Log("INFO", $"发现 {pendingMessages.Count} 条待处理消息");

        foreach (var message in pendingMessages)
        {
            Log("INFO", $"[扫描] 处理消息 ID={message.Id}, TerminalId='{message.TerminalId}', Method='{message.Method}', SourceId='{message.SourceId ?? "无"}'");

            var terminalConfig = _configService.GetTerminalConfig(message.TerminalId);

            if (terminalConfig == null)
            {
                Log("WARN", $"终端 [{message.TerminalId}] 配置不存在，消息 {message.Id} 标记为失败");
                _databaseService.UpdateMessageStatus(message.Id, "Failed", DateTime.Now);
                continue;
            }

            var success = false;
            var errorMsg = "";

            try
            {
                if (message.Method.Equals("text", StringComparison.OrdinalIgnoreCase))
                {
                    success = await SendTextMessageWithSourceAsync(message, terminalConfig);
                }
                else if (message.Method.Equals("image", StringComparison.OrdinalIgnoreCase))
                {
                    success = await SendImageMessageWithSourceAsync(message, terminalConfig);
                }
                else
                {
                    errorMsg = $"不支持的消息类型: {message.Method}";
                    Log("WARN", errorMsg);
                }
            }
            catch (Exception ex)
            {
                success = false;
                errorMsg = ex.Message;
                Log("ERROR", $"发送消息失败: {errorMsg}");
            }

            var newStatus = success ? "Sent" : "Failed";
            _databaseService.UpdateMessageStatus(message.Id, newStatus, DateTime.Now);

            Log(success ? "INFO" : "ERROR",
                $"消息 {message.Id} 已{(success ? "发送成功" : "失败")}，状态: {newStatus}");
        }

        OnStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> SendTextMessageWithSourceAsync(FeishuMessage message, TerminalConfig terminalConfig)
    {
        if (string.IsNullOrWhiteSpace(terminalConfig.TextWebhook))
        {
            Log("WARN", $"终端 [{message.TerminalId}] 文字消息 Webhook 未配置");
            return false;
        }

        string content;
        if (!string.IsNullOrWhiteSpace(message.SourceId))
        {
            Log("INFO", $"[推送] 开始获取消息源数据，SourceId='{message.SourceId}'");
            var fetchResult = await _messageSourceService.FetchAndFormatMessageAsync(message.SourceId, "text");

            if (!fetchResult.Success)
            {
                Log("ERROR", $"获取消息源数据失败: {fetchResult.ErrorMessage}");
                content = $"获取数据失败: {fetchResult.ErrorMessage}";
            }
            else
            {
                content = fetchResult.FormattedText ?? "无数据";
                Log("INFO", $"消息源数据获取成功，内容长度: {content.Length}");
            }
        }
        else
        {
            content = $"收到消息请求 - Terminal: {message.TerminalId}, Method: {message.Method}";
        }

        return SendTextMessage(terminalConfig.TextWebhook, content);
    }

    private async Task<bool> SendImageMessageWithSourceAsync(FeishuMessage message, TerminalConfig terminalConfig)
    {
        if (string.IsNullOrWhiteSpace(terminalConfig.ImageApiKey) ||
            string.IsNullOrWhiteSpace(terminalConfig.ImageSecretKey) ||
            string.IsNullOrWhiteSpace(terminalConfig.ImageReceiverId))
        {
            Log("WARN", $"终端 [{message.TerminalId}] 图片推送配置不完整");
            return false;
        }

        byte[]? imageData = null;
        string? textContent = null;

        if (!string.IsNullOrWhiteSpace(message.SourceId))
        {
            Log("INFO", $"正在获取消息源 [{message.SourceId}] 数据...");
            var fetchResult = await _messageSourceService.FetchAndFormatMessageAsync(message.SourceId, "image");

            if (fetchResult.Success)
            {
                // 优先使用生成的图片数据
                if (fetchResult.ImageData != null && fetchResult.ImageData.Length > 0)
                {
                    imageData = fetchResult.ImageData;
                    Log("INFO", $"消息源图片生成成功，图片大小: {imageData.Length} bytes");
                }
                else if (!string.IsNullOrEmpty(fetchResult.FormattedText))
                {
                    textContent = fetchResult.FormattedText;
                    Log("INFO", $"消息源数据获取成功，使用文字模式");
                }
            }
            else
            {
                Log("ERROR", $"获取消息源数据失败: {fetchResult.ErrorMessage}");
                textContent = $"获取数据失败: {fetchResult.ErrorMessage}";
            }
        }

        // 如果有图片数据，上传并发送图片消息
        if (imageData != null)
        {
            return SendImageWithData(terminalConfig, imageData);
        }

        // 否则发送带文字的图片消息（使用占位图）
        return SendImageMessage(terminalConfig, textContent);
    }

    private bool SendTextMessage(string webhook, string content)
    {
        var payload = new
        {
            msg_type = "text",
            content = new { text = content }
        };

        var json = JsonConvert.SerializeObject(payload);
        var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        var response = _httpClient.PostAsync(webhook, httpContent).Result;
        return response.IsSuccessStatusCode;
    }

    private bool SendImageMessage(TerminalConfig config, string? message = null)
    {
        var token = GetTenantAccessToken(config.ImageApiKey, config.ImageSecretKey);
        if (string.IsNullOrEmpty(token))
        {
            Log("ERROR", "获取飞书 AccessToken 失败");
            return false;
        }

        var imageKey = UploadPlaceholderImage(token);
        if (string.IsNullOrEmpty(imageKey))
        {
            Log("ERROR", "上传图片失败");
            return false;
        }

        return SendImageByKey(token, imageKey, config.ImageReceiverId, message);
    }

    private bool SendImageWithData(TerminalConfig config, byte[] imageData)
    {
        Log("INFO", $"[SendImageWithData] 开始发送图片...");
        Log("INFO", $"[SendImageWithData] AppId: {config.ImageApiKey}, AppSecret: {config.ImageSecretKey}, ReceiverId: {config.ImageReceiverId}");

        if (string.IsNullOrWhiteSpace(config.ImageApiKey) || string.IsNullOrWhiteSpace(config.ImageSecretKey))
        {
            Log("ERROR", $"终端 [{config.TerminalId}] 图片推送配置不完整: AppId='{config.ImageApiKey}', AppSecret='{config.ImageSecretKey}'");
            return false;
        }

        Log("INFO", "[SendImageWithData] 正在获取 AccessToken...");
        var token = GetTenantAccessToken(config.ImageApiKey, config.ImageSecretKey);
        if (string.IsNullOrEmpty(token))
        {
            Log("ERROR", $"[SendImageWithData] 获取飞书 AccessToken 失败");
            return false;
        }

        Log("INFO", $"[SendImageWithData] Token获取成功，开始上传图片...");
        var imageKey = UploadImageData(token, imageData);
        if (string.IsNullOrEmpty(imageKey))
        {
            Log("ERROR", "[SendImageWithData] 上传图片失败");
            return false;
        }

        Log("INFO", $"[SendImageWithData] 图片上传成功，imageKey: {imageKey}，开始发送图片消息...");
        var result = SendImageByKey(token, imageKey, config.ImageReceiverId, null);
        Log("INFO", $"[SendImageWithData] 发送结果: {(result ? "成功" : "失败")}");
        return result;
    }

    private string? GetTenantAccessToken(string apiKey, string secretKey)
    {
        Log("INFO", $"正在获取飞书 AccessToken... AppId: {apiKey}");

        var payload = new { app_id = apiKey, app_secret = secretKey };
        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = _httpClient.PostAsync(
            "https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal",
            content
        ).Result;

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = response.StatusCode;
            var errorBody = response.Content.ReadAsStringAsync().Result;
            Log("ERROR", $"获取 Token 失败: HTTP {(int)statusCode}, Body: {errorBody}");
            return null;
        }

        var responseBody = response.Content.ReadAsStringAsync().Result;
        Log("INFO", $"[GetToken] 响应 Body: {responseBody}");

        var result = JsonConvert.DeserializeObject<FeishuTokenResponse>(responseBody);

        if (result == null)
        {
            Log("ERROR", $"[GetToken] 解析 Token 响应失败");
            return null;
        }

        Log("INFO", $"[GetToken] Code={result.Code}, Msg={result.Msg}, Token={(string.IsNullOrEmpty(result.TenantAccessToken) ? "为空" : result.TenantAccessToken.Substring(0, Math.Min(20, result.TenantAccessToken.Length)) + "...")}");

        if (result.Code != 0)
        {
            Log("ERROR", $"[GetToken] Token获取失败: Code={result.Code}, Msg={result.Msg}");
            return null;
        }

        return result.TenantAccessToken;
    }

    private string? UploadPlaceholderImage(string token)
    {
        var placeholderBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
        );

        using var content = new MultipartFormDataContent();
        var imageContent = new ByteArrayContent(placeholderBytes);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(imageContent, "image", "placeholder.png");
        content.Add(new StringContent("message"), "image_type");

        var request = new HttpRequestMessage(HttpMethod.Post, "https://open.feishu.cn/open-apis/im/v1/images");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = content;

        var response = _httpClient.SendAsync(request).Result;
        if (!response.IsSuccessStatusCode)
            return null;

        var responseBody = response.Content.ReadAsStringAsync().Result;
        var result = JsonConvert.DeserializeObject<FeishuUploadResponse>(responseBody);

        return result?.Data?.ImageKey;
    }

    private string? UploadImageData(string token, byte[] imageData)
    {
        Log("INFO", $"[UploadImageData] 开始上传图片，图片大小: {imageData.Length} bytes");

        using var content = new MultipartFormDataContent();
        var imageContent = new ByteArrayContent(imageData);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(imageContent, "image", "ranking.png");
        content.Add(new StringContent("message"), "image_type");

        var request = new HttpRequestMessage(HttpMethod.Post, "https://open.feishu.cn/open-apis/im/v1/images");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = content;

        Log("INFO", "[UploadImageData] 发送上传请求...");
        var response = _httpClient.SendAsync(request).Result;

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = response.StatusCode;
            var errorBody = response.Content.ReadAsStringAsync().Result;
            Log("ERROR", $"[UploadImageData] 上传失败: HTTP {(int)statusCode}, Body: {errorBody}");
            return null;
        }

        var responseBody = response.Content.ReadAsStringAsync().Result;
        Log("INFO", $"[UploadImageData] 响应: {responseBody}");

        var result = JsonConvert.DeserializeObject<FeishuUploadResponse>(responseBody);
        return result?.Data?.ImageKey;
    }

    private bool SendImageByKey(string token, string imageKey, string receiverId, string? message)
    {
        Log("INFO", $"[SendImageByKey] 开始发送图片消息... imageKey={imageKey}, receiverId={receiverId}");

        object contentObj;
        if (!string.IsNullOrEmpty(message))
        {
            contentObj = new { image_key = imageKey, text = message };
        }
        else
        {
            contentObj = new { image_key = imageKey };
        }

        var payload = new
        {
            receive_id = receiverId,
            msg_type = "image",
            content = JsonConvert.SerializeObject(contentObj)
        };

        var idType = receiverId.StartsWith("ou_") ? "open_id" : "chat_id";
        var url = $"https://open.feishu.cn/open-apis/im/v1/messages?receive_id_type={idType}";
        Log("INFO", $"[SendImageByKey] URL: {url}");

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        var response = _httpClient.SendAsync(request).Result;
        if (!response.IsSuccessStatusCode)
        {
            var statusCode = response.StatusCode;
            var errorBody = response.Content.ReadAsStringAsync().Result;
            Log("ERROR", $"[SendImageByKey] 发送失败: HTTP {(int)statusCode}, Body: {errorBody}");
            return false;
        }

        var responseBody = response.Content.ReadAsStringAsync().Result;
        var result = JsonConvert.DeserializeObject<FeishuSendResponse>(responseBody);

        Log("INFO", $"[SendImageByKey] 响应: {responseBody}");
        Log("INFO", $"[SendImageByKey] 解析结果: Code={result?.Code}");

        return result?.Code == 0;
    }

    private void Log(string level, string message)
    {
        var entry = new LogEntry
        {
            Time = DateTime.Now,
            Level = level,
            Message = message
        };
        OnLog?.Invoke(this, entry);
    }

    public void Dispose()
    {
        Stop();
        _httpClient.Dispose();
        _messageSourceService.Dispose();
    }
}

public class FeishuTokenResponse
{
    [JsonProperty("code")]
    public int Code { get; set; }

    [JsonProperty("msg")]
    public string Msg { get; set; } = "";

    [JsonProperty("tenant_access_token")]
    public string? TenantAccessToken { get; set; }

    [JsonProperty("expire")]
    public int Expire { get; set; }
}

public class FeishuUploadResponse
{
    [JsonProperty("code")]
    public int Code { get; set; }

    [JsonProperty("msg")]
    public string Msg { get; set; } = "";

    [JsonProperty("data")]
    public FeishuUploadData? Data { get; set; }
}

public class FeishuUploadData
{
    [JsonProperty("image_key")]
    public string ImageKey { get; set; } = "";
}

public class FeishuSendResponse
{
    [JsonProperty("code")]
    public int Code { get; set; }

    [JsonProperty("msg")]
    public string Msg { get; set; } = "";

    [JsonProperty("data")]
    public object? Data { get; set; }
}
