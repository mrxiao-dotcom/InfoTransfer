using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using InfoTransfer.Models;

namespace InfoTransfer.Services;

public class DataPushService : IDisposable
{
    private readonly DatabaseService _databaseService;
    private readonly HttpClient _httpClient;
    private System.Threading.Timer? _scanTimer;
    private bool _isRunning;
    private readonly string _tempPath;

    public event EventHandler<LogEntry>? OnLog;
    public event EventHandler? OnStatusChanged;

    public bool IsRunning => _isRunning;

    public DataPushService(DatabaseService databaseService)
    {
        _databaseService = databaseService;
        _httpClient = new HttpClient();
        _tempPath = Path.Combine(Path.GetTempPath(), "InfoTransfer");
        Directory.CreateDirectory(_tempPath);
    }

    public void Start()
    {
        if (_isRunning) return;

        var intervalMs = 10000;

        _scanTimer = new System.Threading.Timer(ScanCallback, null, 0, intervalMs);
        _isRunning = true;

        Log("INFO", "数据推送服务已启动");
        OnStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _scanTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _scanTimer?.Dispose();
        _scanTimer = null;
        _isRunning = false;

        Log("INFO", "数据推送服务已停止");
        OnStatusChanged?.Invoke(this, EventArgs.Empty);
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

    private void ScanAndProcess()
    {
        var tasks = _databaseService.GetEnabledScheduledTasks();

        if (tasks.Count == 0)
            return;

        foreach (var task in tasks)
        {
            if (!ShouldRunTask(task))
                continue;

            ProcessTask(task);
        }

        OnStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool ShouldRunTask(ScheduledTask task)
    {
        if (!task.IsEnabled)
            return false;

        var now = DateTime.Now;
        var currentTime = now.TimeOfDay;

        // 检查是否跨天，需要重置执行记录
        if (task.NeedsReset())
        {
            task.ExecutedToday = "";
            task.LastRunTime = now;
            _databaseService.SaveScheduledTask(task);
            Log("INFO", $"任务 [{task.DisplayName}] 已跨天，重置执行记录");
        }

        // 获取所有配置的时间点
        var scheduleTimes = task.GetScheduleTimeSpans();
        if (scheduleTimes.Count == 0)
            return false;

        // 查找最近的一个待执行时间点（误差在60秒内）
        foreach (var scheduleTime in scheduleTimes)
        {
            var diff = currentTime - scheduleTime;
            if (diff.TotalSeconds >= 0 && diff.TotalSeconds < 60 && !task.HasExecutedToday(scheduleTime))
            {
                return true;
            }
        }

        return false;
    }

    private void ProcessTask(ScheduledTask task)
    {
        var now = DateTime.Now;
        var currentTime = now.TimeOfDay;

        // 找出当前应该执行的时间点
        TimeSpan? executeTime = null;
        var scheduleTimes = task.GetScheduleTimeSpans();
        foreach (var scheduleTime in scheduleTimes)
        {
            var diff = currentTime - scheduleTime;
            if (diff.TotalSeconds >= 0 && diff.TotalSeconds < 60 && !task.HasExecutedToday(scheduleTime))
            {
                executeTime = scheduleTime;
                break;
            }
        }

        if (executeTime == null)
            return;

        Log("INFO", $"执行定时任务: {task.DisplayName} ({executeTime.Value:hh\\:mm})");

        var source = _databaseService.GetAllMessageSources()
            .FirstOrDefault(s => s.SourceId == task.SourceId);

        if (source == null)
        {
            Log("ERROR", $"消息源 [{task.SourceId}] 不存在");
            SaveHistory(task, "Failed", "消息源不存在");
            return;
        }

        var terminal = _databaseService.GetAllTerminalConfigs()
            .FirstOrDefault(t => t.TerminalId == task.TerminalId);

        if (terminal == null)
        {
            Log("ERROR", $"终端 [{task.TerminalId}] 不存在");
            SaveHistory(task, "Failed", "终端不存在");
            return;
        }

        try
        {
            var responseData = CallApi(source);
            if (responseData == null)
            {
                SaveHistory(task, "Failed", "API 调用失败");
                return;
            }

            var success = true;

            if (task.EnableText)
            {
                var textContent = ParseTextContent(responseData, source);
                success = SendTextMessage(terminal, textContent) && success;
            }

            if (task.EnableImage)
            {
                var imagePath = GenerateTableImage(responseData, source, task);
                if (!string.IsNullOrEmpty(imagePath))
                {
                    success = SendImageMessage(terminal, imagePath) && success;
                }
            }

            var status = success ? "Sent" : "Failed";

            // 标记已执行的时间点
            task.MarkExecutedToday(executeTime.Value);
            task.LastRunTime = now;
            _databaseService.SaveScheduledTask(task);

            SaveHistory(task, status, success ? null : "部分推送失败");

            Log("INFO", $"任务 [{task.DisplayName}] 执行完成，状态: {status}");
        }
        catch (Exception ex)
        {
            Log("ERROR", $"任务 [{task.DisplayName}] 执行异常: {ex.Message}");
            SaveHistory(task, "Failed", ex.Message);
        }
    }

    private string? CallApi(MessageSource source)
    {
        try
        {
            var url = source.ApiUrl;
            if (!string.IsNullOrWhiteSpace(source.ApiParameters))
            {
                if (source.ApiMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    url = url + (url.Contains("?") ? "&" : "?") + source.ApiParameters;
                }
            }

            HttpResponseMessage response;
            if (source.ApiMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                response = _httpClient.GetAsync(url).Result;
            }
            else
            {
                var content = new StringContent(source.ApiParameters ?? "{}", Encoding.UTF8, "application/json");
                response = _httpClient.PostAsync(url, content).Result;
            }

            if (!response.IsSuccessStatusCode)
            {
                Log("WARN", $"API 调用失败: {response.StatusCode}");
                return null;
            }

            return response.Content.ReadAsStringAsync().Result;
        }
        catch (Exception ex)
        {
            Log("ERROR", $"API 调用异常: {ex.Message}");
            return null;
        }
    }

    private string ParseTextContent(string responseData, MessageSource source)
    {
        try
        {
            var json = JObject.Parse(responseData);

            if (string.IsNullOrWhiteSpace(source.ResponseFormat))
            {
                return responseData;
            }

            var format = JObject.Parse(source.ResponseFormat);
            var result = new StringBuilder();

            result.AppendLine($"📊 {source.Name}");
            result.AppendLine("═══════════════");

            if (format.ContainsKey("title"))
            {
                result.AppendLine($"【{format["title"]}】");
            }

            if (format.ContainsKey("items"))
            {
                var items = format["items"] as JArray;
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        var field = item["field"]?.ToString();
                        var label = item["label"]?.ToString();

                        if (!string.IsNullOrWhiteSpace(field))
                        {
                            var value = json.SelectToken(field);
                            if (value != null)
                            {
                                result.AppendLine($"{label ?? field}: {value}");
                            }
                        }
                    }
                }
            }

            result.AppendLine("─────────────────");
            result.AppendLine($"更新时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            return result.ToString();
        }
        catch (Exception ex)
        {
            Log("WARN", $"解析文本内容异常: {ex.Message}");
            return responseData;
        }
    }

    private string GenerateTableImage(string responseData, MessageSource source, ScheduledTask task)
    {
        try
        {
            var json = JObject.Parse(responseData);
            var tempFile = Path.Combine(_tempPath, $"push_{task.TaskId}_{DateTime.Now:yyyyMMddHHmmss}.png");

            int width = 600;
            int headerHeight = 50;
            int rowHeight = 35;
            int padding = 15;

            var columns = new List<string> { "排名", "名称", "数值" };
            var rows = new List<string[]>();

            try
            {
                var dataArray = json["data"] as JArray ?? json["list"] as JArray ?? json["items"] as JArray;
                if (dataArray != null)
                {
                    int rank = 1;
                    foreach (var item in dataArray.Take(10))
                    {
                        var name = item["name"]?.ToString() ?? item["title"]?.ToString() ?? item["symbol"]?.ToString() ?? "-";
                        var value = item["value"]?.ToString() ?? item["price"]?.ToString() ?? "-";
                        rows.Add(new[] { rank.ToString(), name, value });
                        rank++;
                    }
                }
                else if (json.Properties().Any())
                {
                    foreach (var prop in json.Properties().Take(10))
                    {
                        rows.Add(new[] { (rows.Count + 1).ToString(), prop.Name, prop.Value?.ToString() ?? "-" });
                    }
                }
            }
            catch
            {
                rows.Add(new[] { "1", "数据", responseData.Length > 50 ? responseData.Substring(0, 50) + "..." : responseData });
            }

            int tableHeight = headerHeight + (rows.Count * rowHeight);
            int height = tableHeight + 80;
            int col1Width = 60;
            int col2Width = (width - col1Width - padding * 2) / 2;
            int col3Width = col2Width;

            using var bitmap = new Bitmap(width, height);
            using var graphics = Graphics.FromImage(bitmap);

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.White);

            using var headerFont = new Font("Microsoft YaHei UI", 14, FontStyle.Bold);
            using var titleFont = new Font("Microsoft YaHei UI", 16, FontStyle.Bold);
            using var cellFont = new Font("Microsoft YaHei UI", 11);
            using var timeFont = new Font("Microsoft YaHei UI", 9);

            using var headerBrush = new SolidBrush(Color.FromArgb(26, 144, 255));
            using var rowBrush1 = new SolidBrush(Color.FromArgb(240, 246, 255));
            using var rowBrush2 = new SolidBrush(Color.White);
            using var textBrush = new SolidBrush(Color.FromArgb(51, 51, 51));
            using var titleBrush = new SolidBrush(Color.FromArgb(250, 140, 22));

            int currentY = padding;

            using (var titleFormat = new StringFormat())
            {
                titleFormat.Alignment = StringAlignment.Center;
                graphics.DrawString(source.Name, titleFont, titleBrush,
                    new RectangleF(0, currentY, width, 30), titleFormat);
            }
            currentY += 35;

            using var headerBgBrush = new SolidBrush(Color.FromArgb(26, 144, 255));
            graphics.FillRectangle(headerBgBrush, padding, currentY, width - padding * 2, headerHeight);

            graphics.DrawString(columns[0], headerFont, Brushes.White, padding + 10, currentY + 12);
            graphics.DrawString(columns[1], headerFont, Brushes.White, padding + col1Width + 10, currentY + 12);
            graphics.DrawString(columns[2], headerFont, Brushes.White, padding + col1Width + col2Width + 10, currentY + 12);

            currentY += headerHeight;

            for (int i = 0; i < rows.Count; i++)
            {
                var bgBrush = i % 2 == 0 ? rowBrush1 : rowBrush2;
                graphics.FillRectangle(bgBrush, padding, currentY, width - padding * 2, rowHeight);

                graphics.DrawString(rows[i][0], cellFont, textBrush, padding + 10, currentY + 8);
                graphics.DrawString(TruncateString(rows[i][1], 20), cellFont, textBrush, padding + col1Width + 10, currentY + 8);
                graphics.DrawString(rows[i][2], cellFont, textBrush, padding + col1Width + col2Width + 10, currentY + 8);

                using var borderPen = new Pen(Color.FromArgb(232, 232, 232), 1);
                graphics.DrawLine(borderPen, padding, currentY + rowHeight, width - padding, currentY + rowHeight);

                currentY += rowHeight;
            }

            currentY += 10;
            graphics.DrawString($"更新时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}", timeFont,
                new SolidBrush(Color.FromArgb(153, 153, 153)), padding, currentY);

            bitmap.Save(tempFile, ImageFormat.Png);
            return tempFile;
        }
        catch (Exception ex)
        {
            Log("ERROR", $"生成图片失败: {ex.Message}");
            return "";
        }
    }

    private string TruncateString(string str, int maxLength)
    {
        if (string.IsNullOrEmpty(str)) return str;
        return str.Length > maxLength ? str.Substring(0, maxLength - 2) + ".." : str;
    }

    private bool SendTextMessage(TerminalConfig terminal, string content)
    {
        if (string.IsNullOrWhiteSpace(terminal.TextWebhook))
        {
            Log("WARN", "文字消息 Webhook 未配置");
            return false;
        }

        try
        {
            var payload = new
            {
                msg_type = "text",
                content = new { text = content }
            };

            var json = JsonConvert.SerializeObject(payload);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            var response = _httpClient.PostAsync(terminal.TextWebhook, httpContent).Result;
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log("ERROR", $"发送文字消息失败: {ex.Message}");
            return false;
        }
    }

    private bool SendImageMessage(TerminalConfig terminal, string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            Log("ERROR", $"图片文件不存在: {imagePath}");
            return false;
        }

        if (string.IsNullOrWhiteSpace(terminal.ImageApiKey) ||
            string.IsNullOrWhiteSpace(terminal.ImageSecretKey) ||
            string.IsNullOrWhiteSpace(terminal.ImageReceiverId))
        {
            Log("WARN", "图片推送配置不完整");
            return false;
        }

        try
        {
            var token = GetTenantAccessToken(terminal.ImageApiKey, terminal.ImageSecretKey);
            if (string.IsNullOrEmpty(token))
            {
                Log("ERROR", "获取飞书 AccessToken 失败");
                return false;
            }

            var imageKey = UploadImage(token, imagePath);
            if (string.IsNullOrEmpty(imageKey))
            {
                Log("ERROR", "上传图片失败");
                return false;
            }

            return SendImageByKey(token, imageKey, terminal.ImageReceiverId);
        }
        finally
        {
            try { File.Delete(imagePath); } catch { }
        }
    }

    private string? GetTenantAccessToken(string apiKey, string secretKey)
    {
        var payload = new { app_id = apiKey, app_secret = secretKey };
        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = _httpClient.PostAsync(
            "https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal",
            content
        ).Result;

        if (!response.IsSuccessStatusCode)
            return null;

        var responseBody = response.Content.ReadAsStringAsync().Result;
        var result = JsonConvert.DeserializeObject<FeishuTokenResponse>(responseBody);

        return result?.TenantAccessToken;
    }

    private string? UploadImage(string token, string imagePath)
    {
        var imageBytes = File.ReadAllBytes(imagePath);

        using var content = new MultipartFormDataContent();
        var imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(imageContent, "image", Path.GetFileName(imagePath));
        content.Add(new StringContent("message"), "image_type");

        var request = new HttpRequestMessage(HttpMethod.Post, "https://open.feishu.cn/open-apis/im/v1/images");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = content;

        var response = _httpClient.SendAsync(request).Result;
        if (!response.IsSuccessStatusCode)
            return null;

        var responseBody = response.Content.ReadAsStringAsync().Result;
        var result = JsonConvert.DeserializeObject<FeishuUploadResponse>(responseBody);

        return result?.Data?.ImageKey;
    }

    private bool SendImageByKey(string token, string imageKey, string receiverId)
    {
        var payload = new
        {
            receive_id = receiverId,
            msg_type = "image",
            content = JsonConvert.SerializeObject(new { image_key = imageKey })
        };

        var idType = receiverId.StartsWith("ou_") ? "open_id" : "chat_id";
        var url = $"https://open.feishu.cn/open-apis/im/v1/messages?receive_id_type={idType}";

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        var response = _httpClient.SendAsync(request).Result;
        if (!response.IsSuccessStatusCode)
            return false;

        var responseBody = response.Content.ReadAsStringAsync().Result;
        var result = JsonConvert.DeserializeObject<FeishuSendResponse>(responseBody);

        return result?.Code == 0;
    }

    private void SaveHistory(ScheduledTask task, string status, string? errorMessage)
    {
        var history = new TaskHistory
        {
            TaskIdStr = task.TaskId,
            Status = status,
            ErrorMessage = errorMessage,
            ExecutedAt = DateTime.Now
        };
        _databaseService.SaveTaskHistory(history);
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
    }
}
