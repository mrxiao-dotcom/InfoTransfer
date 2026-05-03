using System;
using System.Net;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using InfoTransfer.Models;

namespace InfoTransfer.Services;

public class ApiServerService : IDisposable
{
    private readonly DatabaseService _databaseService;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private bool _isRunning;
    private int _port;

    public event EventHandler<LogEntry>? OnLog;
    public event EventHandler? OnStatusChanged;

    public bool IsRunning => _isRunning;
    public int Port => _port;

    public ApiServerService(DatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public void Start(int port)
    {
        if (_isRunning) return;

        _port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");
        _cts = new CancellationTokenSource();

        try
        {
            _listener.Start();
            _listenerTask = Task.Run(() => ListenAsync(_cts.Token));
            _isRunning = true;

            Log("INFO", $"API 服务器已启动，监听端口: {port}");
            OnStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log("ERROR", $"启动服务器失败: {ex.Message}");
            _listener?.Stop();
            _listener = null;
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _cts?.Cancel();
        _listener?.Stop();
        _listenerTask?.Wait(1000);
        _listener?.Close();

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _listenerTask = null;
        _isRunning = false;

        Log("INFO", "API 服务器已停止");
        OnStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (HttpListenerException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("ERROR", $"监听异常: {ex.Message}");
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            var path = request.Url?.AbsolutePath ?? "/";
            var method = request.HttpMethod;

            Log("INFO", $"收到请求: {method} {path}");

            if (path.Equals("/api/feishu/message", StringComparison.OrdinalIgnoreCase) &&
                method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                HandleFeishuMessage(request, response);
            }
            else if (path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) &&
                     method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                HandleHealthCheck(response);
            }
            else
            {
                SendJsonResponse(response, HttpStatusCode.NotFound, new { error = "未找到请求路径" });
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"处理请求异常: {ex.Message}");
            SendJsonResponse(response, HttpStatusCode.InternalServerError, new { error = ex.Message });
        }
    }

    private void HandleFeishuMessage(HttpListenerRequest request, HttpListenerResponse response)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = reader.ReadToEndAsync().Result;

        Log("DEBUG", $"[API] 收到的原始请求: {body}");

        MessageRequest? messageRequest;
        try
        {
            messageRequest = JsonConvert.DeserializeObject<MessageRequest>(body);
        }
        catch
        {
            SendJsonResponse(response, HttpStatusCode.BadRequest, new { error = "无效的 JSON 格式" });
            return;
        }

        if (messageRequest == null ||
            string.IsNullOrWhiteSpace(messageRequest.TerminalId) ||
            string.IsNullOrWhiteSpace(messageRequest.MessageType) ||
            string.IsNullOrWhiteSpace(messageRequest.Method))
        {
            SendJsonResponse(response, HttpStatusCode.BadRequest, new { error = "缺少必填参数" });
            return;
        }

        Log("INFO", $"[API] 解析请求成功 - TerminalId='{messageRequest.TerminalId}', SourceId='{messageRequest.GetSourceId()}', SourceName(兼容)='{messageRequest.SourceName ?? "无"}'");

        var now = DateTime.Now;
        var message = new FeishuMessage
        {
            MessageType = messageRequest.MessageType,
            TerminalId = messageRequest.TerminalId,
            Method = messageRequest.Method,
            Status = "Pending",
            ReceivedAt = now.ToString("o"),
            CreatedAt = now.ToString("o"),
            SourceId = messageRequest.GetSourceId()
        };

        var id = _databaseService.CreateMessage(message);

        Log("INFO", $"创建消息记录成功，ID: {id}, Terminal: {message.TerminalId}, Source: {message.SourceId ?? "无"}");

        message.Id = id;
        SendJsonResponse(response, HttpStatusCode.OK, new ApiResponse<FeishuMessage>
        {
            Success = true,
            Data = message,
            Message = "消息请求已记录"
        });

        OnStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleHealthCheck(HttpListenerResponse response)
    {
        var counts = _databaseService.GetMessageCounts();
        var result = new
        {
            status = "running",
            pending = counts.Pending,
            sent = counts.Sent,
            failed = counts.Failed
        };

        SendJsonResponse(response, HttpStatusCode.OK, result);
    }

    private void SendJsonResponse(HttpListenerResponse response, HttpStatusCode statusCode, object? data)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";

        var json = JsonConvert.SerializeObject(data, Formatting.Indented);
        var buffer = Encoding.UTF8.GetBytes(json);

        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
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
    }
}
