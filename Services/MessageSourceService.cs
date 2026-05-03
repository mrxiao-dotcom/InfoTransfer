using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using InfoTransfer.Models;

namespace InfoTransfer.Services;

public class MessageSourceService
{
    private readonly HttpClient _httpClient;
    private readonly DatabaseService _databaseService;
    private readonly ImageGeneratorService _imageGenerator;
    public event EventHandler<LogEntry>? OnLog;

    public MessageSourceService(DatabaseService databaseService)
    {
        _databaseService = databaseService;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _imageGenerator = new ImageGeneratorService();
    }

    public class FetchResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? RawData { get; set; }
        public string? FormattedText { get; set; }
        public byte[]? ImageData { get; set; }
    }

    public async Task<FetchResult> FetchAndFormatMessageAsync(string sourceName, string method)
    {
        var result = new FetchResult();

        Log($"[消息源] 开始获取消息源，sourceName='{sourceName}', method='{method}'");

        var source = _databaseService.GetMessageSourceByName(sourceName);
        if (source == null)
        {
            result.Success = false;
            result.ErrorMessage = $"未找到消息源配置: {sourceName}";
            Log($"[消息源] 未找到消息源配置，尝试通过名称或ID '{sourceName}' 查找失败");

            // 列出所有已配置的消息源，帮助调试
            var allSources = _databaseService.GetAllMessageSources();
            Log($"[消息源] 当前数据库中共有 {allSources.Count} 个消息源配置:");
            foreach (var s in allSources)
            {
                Log($"       - SourceId: '{s.SourceId}', Name: '{s.Name}'");
            }

            return result;
        }

        Log($"[消息源] 找到消息源配置: SourceId='{source.SourceId}', Name='{source.Name}', ApiUrl='{source.ApiUrl}'");

        // 记录 Token 配置状态（不显示具体值）
        if (!string.IsNullOrWhiteSpace(source.ApiToken))
        {
            Log($"[消息源] Token 已配置，将用于认证");
        }
        else
        {
            Log($"[消息源] Token 未配置");
        }

        if (string.IsNullOrWhiteSpace(source.ApiUrl))
        {
            result.Success = false;
            result.ErrorMessage = $"消息源 [{sourceName}] 未配置 API 地址";
            return result;
        }

        try
        {
            string rawData;
            if (source.ApiMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                rawData = await PostAsync(source.ApiUrl, source.ApiParameters);
            }
            else
            {
                rawData = await GetAsync(source.ApiUrl, source.ApiParameters, source.ApiToken);
            }

            result.Success = true;
            result.RawData = rawData;

            if (method.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                result.FormattedText = FormatTextMessage(rawData, source.ResponseFormat, sourceName);
            }
            else if (method.Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                // 图片模式：生成图片数据
                var imageData = GenerateImageFromData(rawData, source.ResponseFormat, sourceName);
                if (imageData != null)
                {
                    result.ImageData = imageData;
                    result.FormattedText = "图片消息"; // 占位文本
                }
                else
                {
                    // 生成图片失败时，尝试返回文字格式
                    result.FormattedText = FormatTextMessage(rawData, source.ResponseFormat, sourceName);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"API 调用失败: {ex.Message}";
            return result;
        }
    }

    private async Task<string> GetAsync(string url, string? parameters, string? apiToken)
    {
        var fullUrl = url;

        // 替换 URL 中的占位符
        if (!string.IsNullOrWhiteSpace(parameters))
        {
            // 替换 {date} 占位符
            if (parameters.Contains("date="))
            {
                var dateMatch = System.Text.RegularExpressions.Regex.Match(parameters, @"date=(\d{4}-\d{2}-\d{2})");
                if (dateMatch.Success)
                {
                    var dateValue = dateMatch.Groups[1].Value;
                    fullUrl = fullUrl.Replace("{date}", dateValue);
                }
            }
            else if (fullUrl.Contains("{date}"))
            {
                var latestDate = GetLatestTradingDate();
                fullUrl = fullUrl.Replace("{date}", latestDate);
            }

            // 添加其他查询参数
            var queryParams = System.Text.RegularExpressions.Regex.Replace(parameters, @"date=\d{4}-\d{2}-\d{2}&?", "").TrimEnd('&');
            if (!string.IsNullOrEmpty(queryParams))
            {
                if (fullUrl.Contains('?'))
                    fullUrl += "&" + queryParams;
                else
                    fullUrl += "?" + queryParams;
            }
        }
        else if (fullUrl.Contains("{date}"))
        {
            var latestDate = GetLatestTradingDate();
            fullUrl = fullUrl.Replace("{date}", latestDate);
        }

        // 如果 URL 包含日期但没有 {date} 占位符，替换为实际日期
        if (fullUrl.Contains("{date}") == false)
        {
            // 情况1: URL 格式为 /date/YYYY-MM-DD
            var dateMatch = System.Text.RegularExpressions.Regex.Match(fullUrl, @"/date/(\d{4}-\d{2}-\d{2})");
            if (dateMatch.Success)
            {
                var urlDate = dateMatch.Groups[1].Value;
                // 检查是否是未来日期
                if (DateTime.TryParse(urlDate, out var parsedDate) && parsedDate.Date > DateTime.Now.Date)
                {
                    var latestDate = GetLatestTradingDate();
                    fullUrl = fullUrl.Replace($"/date/{urlDate}", $"/date/{latestDate}");
                }
            }
            // 情况2: URL 格式为 /date/ (末尾无日期或只有斜杠)
            else if (fullUrl.Contains("/date/") && !System.Text.RegularExpressions.Regex.IsMatch(fullUrl, @"/date/\d{4}-\d{2}-\d{2}"))
            {
                var latestDate = GetLatestTradingDate();
                // 处理末尾斜杠的情况: /date/ -> /date/YYYY-MM-DD
                fullUrl = System.Text.RegularExpressions.Regex.Replace(fullUrl, @"/date/\??$", $"/date/{latestDate}");
                // 如果替换后末尾还有问号（原有查询参数），去掉末尾的问号
                fullUrl = fullUrl.TrimEnd('?');
            }
        }

        Log($"[消息源] 请求 URL: {fullUrl}");

        // 添加认证 Header
        var request = new HttpRequestMessage(HttpMethod.Get, fullUrl);
        if (!string.IsNullOrWhiteSpace(apiToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);
        }

        // 尝试请求，如果失败则尝试前一天
        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                     response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Log($"[消息源] API 返回 {response.StatusCode}，尝试获取历史数据...");
                for (int daysAgo = 1; daysAgo <= 7; daysAgo++)
                {
                    var historyDate = DateTime.Now.AddDays(-daysAgo);
                    if (historyDate.DayOfWeek == DayOfWeek.Saturday || historyDate.DayOfWeek == DayOfWeek.Sunday)
                        continue;

                    var historyUrl = fullUrl;
                    var dateMatch = System.Text.RegularExpressions.Regex.Match(historyUrl, @"/date/\d{4}-\d{2}-\d{2}");
                    if (dateMatch.Success)
                    {
                        var dateStr = historyDate.ToString("yyyy-MM-dd");
                        historyUrl = System.Text.RegularExpressions.Regex.Replace(historyUrl, @"/date/\d{4}-\d{2}-\d{2}", $"/date/{dateStr}");

                        Log($"[消息源] 尝试历史日期: {dateStr}");
                        var historyRequest = new HttpRequestMessage(HttpMethod.Get, historyUrl);
                        if (!string.IsNullOrWhiteSpace(apiToken))
                        {
                            historyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);
                        }
                        response = await _httpClient.SendAsync(historyRequest);
                        if (response.IsSuccessStatusCode)
                        {
                            return await response.Content.ReadAsStringAsync();
                        }
                    }
                }
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException ex)
        {
            Log($"[消息源] API 请求失败: {ex.Message}");
            throw;
        }
    }

    private string GetLatestTradingDate()
    {
        // 获取最新交易日（工作日）
        var date = DateTime.Now;
        // 如果是周末，往前推到周五
        while (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
        {
            date = date.AddDays(-1);
        }
        return date.ToString("yyyy-MM-dd");
    }

    private async Task<string> PostAsync(string url, string? parameters)
    {
        var content = string.IsNullOrWhiteSpace(parameters) 
            ? new StringContent("{}", Encoding.UTF8, "application/json")
            : new StringContent(parameters, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private string FormatTextMessage(string rawData, string responseFormat, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(responseFormat))
        {
            // 首先尝试检测标准格式
            if (rawData.Trim().StartsWith("{") && rawData.Contains("\"longs\"") && rawData.Contains("\"shorts\""))
            {
                return FormatMultiShortRanking(rawData, sourceName);
            }
            
            // 尝试解析 JSON 并递归查找多空数据
            try
            {
                var data = JToken.Parse(rawData);
                var (longs, shorts) = FindMultiShortData(data);
                if (longs != null && shorts != null)
                {
                    return FormatMultiShortRanking(rawData, sourceName);
                }
            }
            catch { }
            
            // 尝试检测流畅度格式
            if (rawData.Trim().StartsWith("{") && rawData.Contains("\"topRanking\"") && rawData.Contains("\"bottomRanking\""))
            {
                return FormatFluencyRanking(rawData, sourceName);
            }
            
            // 尝试解析 JSON 并递归查找流畅度数据
            try
            {
                var data = JToken.Parse(rawData);
                var topRanking = FindValue(data, "topRanking");
                var bottomRanking = FindValue(data, "bottomRanking");
                if (topRanking != null || bottomRanking != null)
                {
                    return FormatFluencyRanking(rawData, sourceName);
                }
            }
            catch { }

            // 尝试检测每日策略精选格式 (data.title + data.summary/content)
            if (rawData.Trim().StartsWith("{") && rawData.Contains("\"summary\"") && rawData.Contains("\"title\""))
            {
                try
                {
                    var data = JToken.Parse(rawData);
                    var success = data["success"]?.Value<bool>();
                    if (success == true || success == null)
                    {
                        var dataObj = data["data"] as JObject;
                        if (dataObj != null && dataObj["title"] != null)
                        {
                            return FormatDailyStrategy(rawData, sourceName);
                        }
                    }
                }
                catch { }
            }

            return FormatDefaultText(rawData, sourceName);
        }

        try
        {
            var data = JToken.Parse(rawData);
            var format = responseFormat.Trim();

            // 特殊格式：多空排行
            if (format.Equals("multi_short_ranking", StringComparison.OrdinalIgnoreCase) ||
                format.Equals("多空排行", StringComparison.OrdinalIgnoreCase))
            {
                return FormatMultiShortRanking(rawData, sourceName);
            }

            // 特殊格式：流畅度排行
            if (format.Equals("fluency_ranking", StringComparison.OrdinalIgnoreCase) ||
                format.Equals("流畅度排行", StringComparison.OrdinalIgnoreCase))
            {
                return FormatFluencyRanking(rawData, sourceName);
            }

            // 特殊格式：每日策略精选
            if (format.Equals("daily_strategy", StringComparison.OrdinalIgnoreCase) ||
                format.Equals("每日策略精选", StringComparison.OrdinalIgnoreCase))
            {
                return FormatDailyStrategy(rawData, sourceName);
            }

            if (format.StartsWith("{") && format.EndsWith("}"))
            {
                return FormatFromTemplate(data, format, sourceName);
            }
            else if (format.StartsWith("$"))
            {
                return FormatFromJsonPath(data, format, sourceName);
            }
            else
            {
                return FormatFromTemplate(data, format, sourceName);
            }
        }
        catch
        {
            return FormatDefaultText(rawData, sourceName);
        }
    }

    private string FormatMultiShortRanking(string rawData, string sourceName)
    {
        try
        {
            var data = JObject.Parse(rawData);
            var sb = new StringBuilder();

            // 转换 sourceName 为友好标题
            var title = GetFriendlyTitle(sourceName, "综合排名");
            sb.AppendLine($"【{title}】");
            sb.AppendLine();

            // 处理多头排行
            var longs = data["longs"] as JArray;
            if (longs != null && longs.Count > 0)
            {
                sb.AppendLine("📈 多头排行 TOP 10");
                sb.AppendLine("━━━━━━━━━━━━━━━━");
                for (int i = 0; i < Math.Min(longs.Count, 10); i++)
                {
                    var item = longs[i];
                    var symbol = item["symbol"]?.ToString() ?? "-";
                    var score = item["score"]?.ToString() ?? "-";
                    sb.AppendLine($"{i + 1,2}. {symbol,-8} {score}");
                }
                sb.AppendLine();
            }

            // 处理空头排行
            var shorts = data["shorts"] as JArray;
            if (shorts != null && shorts.Count > 0)
            {
                sb.AppendLine("📉 空头排行 TOP 10");
                sb.AppendLine("━━━━━━━━━━━━━━━━");
                for (int i = 0; i < Math.Min(shorts.Count, 10); i++)
                {
                    var item = shorts[i];
                    var symbol = item["symbol"]?.ToString() ?? "-";
                    var score = item["score"]?.ToString() ?? "-";
                    sb.AppendLine($"{i + 1,2}. {symbol,-8} {score}");
                }
            }

            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            Log($"[格式化] 多空排行解析失败: {ex.Message}");
            return FormatDefaultText(rawData, sourceName);
        }
    }

    private string FormatFluencyRanking(string rawData, string sourceName)
    {
        try
        {
            var data = JObject.Parse(rawData);
            var sb = new StringBuilder();

            // 转换 sourceName 为友好标题
            var title = GetFriendlyTitle(sourceName, "流畅度排名");
            sb.AppendLine($"【{title}】");

            // 获取交易日期
            var tradeDate = data["data"]?["tradeDate"]?.ToString();
            if (!string.IsNullOrEmpty(tradeDate))
            {
                var datePart = tradeDate.Split('T')[0];
                sb.AppendLine($"交易日期: {datePart}");
            }
            sb.AppendLine();

            // 处理流畅度排行（top）
            var topRanking = data["data"]?["topRanking"] as JArray;
            if (topRanking != null && topRanking.Count > 0)
            {
                sb.AppendLine("📈 流畅度排行 TOP 10");
                sb.AppendLine("━━━━━━━━━━━━━━━━");
                for (int i = 0; i < Math.Min(topRanking.Count, 10); i++)
                {
                    var item = topRanking[i];
                    var symbol = item["symbol"]?.ToString() ?? "-";
                    var smoothness = item["smoothness"]?.ToString() ?? "-";
                    sb.AppendLine($"{i + 1,2}. {symbol,-8} {smoothness,8}");
                }
                sb.AppendLine();
            }

            // 处理流畅度排行（bottom）
            var bottomRanking = data["data"]?["bottomRanking"] as JArray;
            if (bottomRanking != null && bottomRanking.Count > 0)
            {
                sb.AppendLine("📉 流畅度排行 BOTTOM 10");
                sb.AppendLine("━━━━━━━━━━━━━━━━");
                for (int i = 0; i < Math.Min(bottomRanking.Count, 10); i++)
                {
                    var item = bottomRanking[i];
                    var symbol = item["symbol"]?.ToString() ?? "-";
                    var smoothness = item["smoothness"]?.ToString() ?? "-";
                    sb.AppendLine($"{i + 1,2}. {symbol,-8} {smoothness,8}");
                }
            }

            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            Log($"[格式化] 流畅度排行解析失败: {ex.Message}");
            return FormatDefaultText(rawData, sourceName);
        }
    }

    private string FormatDailyStrategy(string rawData, string sourceName)
    {
        try
        {
            var data = JObject.Parse(rawData);
            var sb = new StringBuilder();

            // 转换 sourceName 为友好标题
            var title = GetFriendlyTitle(sourceName, "每日策略精选");
            sb.AppendLine($"【{title}】");
            sb.AppendLine();

            // 检查 success 字段
            var success = data["success"]?.Value<bool>() ?? true;
            if (!success)
            {
                sb.AppendLine("获取数据失败，请稍后重试");
                return sb.ToString().Trim();
            }

            // 获取 data 字段
            var dataObj = data["data"] as JObject;
            if (dataObj == null)
            {
                sb.AppendLine("暂无数据");
                return sb.ToString().Trim();
            }

            // 获取标题、摘要和内容
            var strategyTitle = dataObj["title"]?.ToString();
            var strategySummary = dataObj["summary"]?.ToString();
            var strategyContent = dataObj["content"]?.ToString();

            if (!string.IsNullOrEmpty(strategyTitle))
            {
                sb.AppendLine($"📌 {strategyTitle}");
                sb.AppendLine();
            }

            // 优先使用完整内容字段
            if (!string.IsNullOrEmpty(strategyContent))
            {
                // 内容截断到 4000 字符以适应消息限制
                var maxLength = 4000;
                if (strategyContent.Length > maxLength)
                {
                    strategyContent = strategyContent.Substring(0, maxLength) + "\n\n...(内容已截断)";
                }
                sb.AppendLine(strategyContent);
            }
            else if (!string.IsNullOrEmpty(strategySummary))
            {
                // 如果没有内容，使用摘要
                sb.AppendLine(strategySummary);
            }
            else
            {
                sb.AppendLine("暂无内容");
            }

            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            Log($"[格式化] 每日策略精选解析失败: {ex.Message}");
            return FormatDefaultText(rawData, sourceName);
        }
    }

    private byte[]? GenerateImageFromData(string rawData, string responseFormat, string sourceName)
    {
        try
        {
            // 检查是否是标准多空排行格式
            if (rawData.Trim().StartsWith("{") && rawData.Contains("\"longs\"") && rawData.Contains("\"shorts\""))
            {
                return _imageGenerator.GenerateRankingImage(rawData, sourceName);
            }

            // 检查是否是流畅度排行格式 (data.topRanking / data.bottomRanking)
            if (rawData.Trim().StartsWith("{") && rawData.Contains("\"topRanking\"") && rawData.Contains("\"bottomRanking\""))
            {
                return _imageGenerator.GenerateFluencyRankingImage(rawData, sourceName);
            }

            // 其他格式暂不支持图片生成
            Log($"[图片生成] 数据格式不支持图片生成，将使用文字模式");
            return null;
        }
        catch (Exception ex)
        {
            Log($"[图片生成] 生成图片失败: {ex.Message}");
            return null;
        }
    }

    private string FormatFromTemplate(JToken data, string template, string sourceName)
    {
        var result = template;

        var matches = System.Text.RegularExpressions.Regex.Matches(template, @"\{\{([^}]+)\}\}");
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var path = match.Groups[1].Value.Trim();
            var value = GetJsonValue(data, path);
            result = result.Replace(match.Value, value ?? "");
        }

        if (!result.Contains("{{"))
        {
            return result;
        }

        matches = System.Text.RegularExpressions.Regex.Matches(template, @"\{\$([^}]+)\}\}");
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var path = match.Groups[1].Value.Trim();
            var value = GetJsonValue(data, path);
            result = result.Replace(match.Value, value ?? "");
        }

        return result;
    }

    private string FormatFromJsonPath(JToken data, string format, string sourceName)
    {
        var path = format.Substring(1).Trim();
        var value = GetJsonValue(data, path);

        if (value != null)
        {
            return $"【{sourceName}】\n{value}";
        }

        return $"【{sourceName}】\n{data.ToString(Formatting.Indented)}";
    }

    private string? GetJsonValue(JToken data, string path)
    {
        try
        {
            var parts = path.Split('.');
            JToken? current = data;

            foreach (var part in parts)
            {
                if (current == null) return null;

                if (part.Contains('['))
                {
                    var arrayMatch = System.Text.RegularExpressions.Regex.Match(part, @"(\w+)\[(\d+)\]");
                    if (arrayMatch.Success)
                    {
                        var propName = arrayMatch.Groups[1].Value;
                        var index = int.Parse(arrayMatch.Groups[2].Value);
                        current = current[propName]?[index];
                    }
                }
                else
                {
                    current = current[part];
                }
            }

            if (current is JArray arr)
            {
                return FormatArrayData(arr);
            }
            else if (current != null)
            {
                return current.ToString();
            }
        }
        catch { }

        return null;
    }

    private string FormatArrayData(JArray arr)
    {
        if (arr.Count == 0) return "无数据";

        var sb = new StringBuilder();

        for (int i = 0; i < Math.Min(arr.Count, 10); i++)
        {
            var item = arr[i];
            if (item is JObject obj)
            {
                sb.AppendLine($"{i + 1}. {obj.ToString(Formatting.None)}");
            }
            else
            {
                sb.AppendLine($"{i + 1}. {item}");
            }
        }

        if (arr.Count > 10)
        {
            sb.AppendLine($"... 共 {arr.Count} 条");
        }

        return sb.ToString().Trim();
    }

    private string GetFriendlyTitle(string sourceName, string defaultTitle)
    {
        // 如果 sourceName 是数字ID，转换为友好标题
        if (sourceName == "1" || sourceName.Equals("多空排名", StringComparison.OrdinalIgnoreCase))
        {
            return "综合排名";
        }
        if (sourceName == "2" || sourceName.Equals("流畅度排名", StringComparison.OrdinalIgnoreCase))
        {
            return "流畅度排名";
        }
        if (sourceName == "3" || sourceName.Equals("每日策略精选", StringComparison.OrdinalIgnoreCase))
        {
            return "每日策略精选";
        }
        // 如果已经是友好名称，直接返回
        if (!string.IsNullOrEmpty(sourceName) && !int.TryParse(sourceName, out _))
        {
            return sourceName;
        }
        return defaultTitle;
    }

    private void Log(string message)
    {
        var entry = new LogEntry
        {
            Time = DateTime.Now,
            Level = "DEBUG",
            Message = message
        };
        OnLog?.Invoke(this, entry);
    }

    private (JArray? longs, JArray? shorts) FindMultiShortData(JToken token)
    {
        if (token is JObject obj)
        {
            // 检查当前层是否有 longs 和 shorts
            var longs = token["longs"] as JArray;
            var shorts = token["shorts"] as JArray;
            if (longs != null && shorts != null)
            {
                return (longs, shorts);
            }

            // 递归搜索子节点
            foreach (var prop in obj.Properties())
            {
                var (foundLongs, foundShorts) = FindMultiShortData(prop.Value);
                if (foundLongs != null && foundShorts != null)
                {
                    return (foundLongs, foundShorts);
                }
            }
        }
        else if (token is JArray arr)
        {
            foreach (var item in arr)
            {
                var (foundLongs, foundShorts) = FindMultiShortData(item);
                if (foundLongs != null && foundShorts != null)
                {
                    return (foundLongs, foundShorts);
                }
            }
        }

        return (null, null);
    }

    private JToken? FindValue(JToken token, string key)
    {
        if (token is JObject obj)
        {
            var value = token[key];
            if (value != null)
            {
                return value;
            }

            foreach (var prop in obj.Properties())
            {
                var found = FindValue(prop.Value, key);
                if (found != null)
                {
                    return found;
                }
            }
        }
        else if (token is JArray arr)
        {
            foreach (var item in arr)
            {
                var found = FindValue(item, key);
                if (found != null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private string FormatDefaultText(string rawData, string sourceName)
    {
        try
        {
            var data = JToken.Parse(rawData);

            if (data is JArray arr)
            {
                return FormatArrayData(arr);
            }
            else if (data is JObject obj)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"【{sourceName}】");

                foreach (var prop in obj.Properties())
                {
                    sb.AppendLine($"{prop.Name}: {prop.Value}");
                }

                return sb.ToString();
            }
            else
            {
                return $"【{sourceName}】\n{rawData}";
            }
        }
        catch
        {
            return $"【{sourceName}】\n{rawData}";
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
