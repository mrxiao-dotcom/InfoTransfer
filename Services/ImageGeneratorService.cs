using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace InfoTransfer.Services;

public class ImageGeneratorService
{
    private readonly string _tempFolder;

    public ImageGeneratorService()
    {
        _tempFolder = Path.Combine(Path.GetTempPath(), "InfoTransfer");
        if (!Directory.Exists(_tempFolder))
        {
            Directory.CreateDirectory(_tempFolder);
        }
    }

    public byte[]? GenerateRankingImage(string rawData, string sourceName)
    {
        try
        {
            var data = JObject.Parse(rawData);
            var longs = data["longs"] as JArray;
            var shorts = data["shorts"] as JArray;

            if (longs == null && shorts == null)
            {
                return null;
            }

            // 转换 sourceName 为友好标题
            var title = GetFriendlyTitle(sourceName, "综合排名");

            // 图片尺寸
            const int margin = 40;
            const int headerHeight = 50;
            const int rowHeight = 32;
            const int colRankWidth = 50;
            const int colSymbolWidth = 100;
            const int colScoreWidth = 100;
            const int tableWidth = margin * 2 + colRankWidth + colSymbolWidth + colScoreWidth;

            int longsCount = longs?.Count ?? 0;
            int shortsCount = shorts?.Count ?? 0;
            int maxRows = Math.Max(longsCount, shortsCount);
            int sectionHeight = headerHeight + (maxRows * rowHeight) + 20;
            int titleHeight = 60;
            int totalHeight = titleHeight + sectionHeight * 2 + margin * 2;

            using var bitmap = new Bitmap(tableWidth, totalHeight);
            using var graphics = Graphics.FromImage(bitmap);

            // 设置高质量渲染
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // 背景
            using var bgBrush = new SolidBrush(Color.FromArgb(30, 30, 35));
            graphics.FillRectangle(bgBrush, 0, 0, tableWidth, totalHeight);

            int currentY = margin;

            // 标题
            DrawTitle(graphics, tableWidth, ref currentY, title);

            // 多头排行
            if (longs != null && longs.Count > 0)
            {
                DrawSection(graphics, margin, tableWidth, ref currentY, "📈 多头排行 TOP 10", longs, rowHeight, headerHeight, Color.FromArgb(46, 204, 113), Color.FromArgb(39, 174, 96));
            }

            currentY += 20;

            // 空头排行
            if (shorts != null && shorts.Count > 0)
            {
                DrawSection(graphics, margin, tableWidth, ref currentY, "📉 空头排行 TOP 10", shorts, rowHeight, headerHeight, Color.FromArgb(231, 76, 60), Color.FromArgb(192, 57, 43));
            }

            // 保存为 PNG
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"生成排行图片失败: {ex.Message}");
            return null;
        }
    }

    public byte[]? GenerateFluencyRankingImage(string rawData, string sourceName)
    {
        try
        {
            var data = JObject.Parse(rawData);
            var dataObj = data["data"] as JObject;
            var topRanking = dataObj?["topRanking"] as JArray;
            var bottomRanking = dataObj?["bottomRanking"] as JArray;

            if ((topRanking == null || topRanking.Count == 0) && (bottomRanking == null || bottomRanking.Count == 0))
            {
                return null;
            }

            // 获取交易日期
            var tradeDate = dataObj?["tradeDate"]?.ToString() ?? "";

            // 转换 sourceName 为友好标题
            var title = GetFriendlyTitle(sourceName, "流畅度排名");

            // 图片尺寸
            const int margin = 40;
            const int headerHeight = 40;
            const int rowHeight = 30;
            const int colRankWidth = 50;
            const int colSymbolWidth = 100;
            const int colSmoothnessWidth = 100;
            const int tableWidth = margin * 2 + colRankWidth + colSymbolWidth + colSmoothnessWidth;

            int topRows = Math.Min(topRanking?.Count ?? 0, 10);
            int bottomRows = Math.Min(bottomRanking?.Count ?? 0, 10);
            int sectionHeight = headerHeight + (topRows * rowHeight) + 20;
            int section2Height = bottomRows > 0 ? headerHeight + (bottomRows * rowHeight) + 20 : 0;
            int titleHeight = 60;
            int totalHeight = titleHeight + sectionHeight + section2Height + margin * 3;

            using var bitmap = new Bitmap(tableWidth, totalHeight);
            using var graphics = Graphics.FromImage(bitmap);

            // 设置高质量渲染
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // 背景
            using var bgBrush = new SolidBrush(Color.FromArgb(30, 30, 35));
            graphics.FillRectangle(bgBrush, 0, 0, tableWidth, totalHeight);

            int currentY = margin;

            // 标题
            DrawFluencyTitle(graphics, tableWidth, ref currentY, title, tradeDate);

            // TOP 排行
            if (topRanking != null && topRanking.Count > 0)
            {
                DrawFluencyTopSection(graphics, margin, tableWidth, ref currentY, "📈 流畅度排行 TOP 10", topRanking, rowHeight, headerHeight);
            }

            // BOTTOM 排行
            if (bottomRanking != null && bottomRanking.Count > 0)
            {
                currentY += 15;
                DrawFluencyBottomSection(graphics, margin, tableWidth, ref currentY, "📉 流畅度排行 BOTTOM 10", bottomRanking, rowHeight, headerHeight);
            }

            // 保存为 PNG
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"生成流畅度排行图片失败: {ex.Message}");
            return null;
        }
    }

    private void DrawFluencyTitle(Graphics g, int width, ref int y, string sourceName, string tradeDate)
    {
        using var titleFont = new Font("Microsoft YaHei UI", 16, FontStyle.Bold);
        using var timeFont = new Font("Microsoft YaHei UI", 10);
        using var titleBrush = new SolidBrush(Color.White);
        using var timeBrush = new SolidBrush(Color.FromArgb(180, 180, 180));

        string title = $"【{sourceName}】";
        SizeF titleSize = g.MeasureString(title, titleFont);
        float titleX = (width - titleSize.Width) / 2;

        g.DrawString(title, titleFont, titleBrush, titleX, y);

        if (!string.IsNullOrEmpty(tradeDate))
        {
            var datePart = tradeDate.Split('T')[0];
            string time = $"交易日期: {datePart}";
            SizeF timeSize = g.MeasureString(time, timeFont);
            float timeX = (width - timeSize.Width) / 2;
            g.DrawString(time, timeFont, timeBrush, timeX, y + 26);
        }

        y += 50;
    }

    private void DrawFluencyTopSection(Graphics g, int margin, int width, ref int y, string title, JArray items, int rowHeight, int headerHeight)
    {
        const int colRankWidth = 50;
        const int colSymbolWidth = 100;
        const int colSmoothnessWidth = 100;

        // 标题
        using var titleFont = new Font("Microsoft YaHei UI", 13, FontStyle.Bold);
        using var titleBrush = new SolidBrush(Color.FromArgb(255, 255, 255));
        g.DrawString(title, titleFont, titleBrush, margin, y);
        y += 30;

        // 表头
        using var headerBgBrush = new SolidBrush(Color.FromArgb(46, 204, 113));
        g.FillRectangle(headerBgBrush, margin, y, width - margin * 2, headerHeight);

        using var headerFont = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
        using var headerTextBrush = new SolidBrush(Color.White);

        int colX = margin + 5;
        g.DrawString("排名", headerFont, headerTextBrush, colX, y + 10);
        colX += colRankWidth;
        g.DrawString("品种", headerFont, headerTextBrush, colX, y + 10);
        colX += colSymbolWidth;
        g.DrawString("流畅度", headerFont, headerTextBrush, colX, y + 10);

        y += headerHeight;

        // 数据行
        using var rowFont = new Font("Microsoft YaHei UI", 10);
        int maxRows = Math.Min(items.Count, 10);

        for (int i = 0; i < maxRows; i++)
        {
            var item = items[i];
            var symbol = item["symbol"]?.ToString() ?? "-";
            var smoothness = item["smoothness"]?.ToString() ?? "-";

            // 斑马纹
            if (i % 2 == 0)
            {
                using var zebraBrush = new SolidBrush(Color.FromArgb(40, 40, 48));
                g.FillRectangle(zebraBrush, margin, y, width - margin * 2, rowHeight);
            }

            // 排名颜色
            Color rankColor;
            if (i == 0) rankColor = Color.FromArgb(255, 215, 0);
            else if (i == 1) rankColor = Color.FromArgb(192, 192, 192);
            else if (i == 2) rankColor = Color.FromArgb(205, 127, 50);
            else rankColor = Color.FromArgb(180, 180, 180);

            using var rankBrush = new SolidBrush(rankColor);
            using var textBrush = new SolidBrush(Color.FromArgb(220, 220, 220));

            colX = margin + 5;
            g.DrawString($"#{i + 1}", rowFont, rankBrush, colX, y + 6);
            colX += colRankWidth;
            g.DrawString(symbol, rowFont, textBrush, colX, y + 6);
            colX += colSymbolWidth;
            g.DrawString(smoothness, rowFont, textBrush, colX, y + 6);

            y += rowHeight;
        }
    }

    private void DrawFluencyBottomSection(Graphics g, int margin, int width, ref int y, string title, JArray items, int rowHeight, int headerHeight)
    {
        const int colRankWidth = 50;
        const int colSymbolWidth = 100;
        const int colSmoothnessWidth = 100;

        // 标题
        using var titleFont = new Font("Microsoft YaHei UI", 13, FontStyle.Bold);
        using var titleBrush = new SolidBrush(Color.FromArgb(255, 255, 255));
        g.DrawString(title, titleFont, titleBrush, margin, y);
        y += 30;

        // 表头
        using var headerBgBrush = new SolidBrush(Color.FromArgb(231, 76, 60));
        g.FillRectangle(headerBgBrush, margin, y, width - margin * 2, headerHeight);

        using var headerFont = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
        using var headerTextBrush = new SolidBrush(Color.White);

        int colX = margin + 5;
        g.DrawString("排名", headerFont, headerTextBrush, colX, y + 10);
        colX += colRankWidth;
        g.DrawString("品种", headerFont, headerTextBrush, colX, y + 10);
        colX += colSymbolWidth;
        g.DrawString("流畅度", headerFont, headerTextBrush, colX, y + 10);

        y += headerHeight;

        // 数据行
        using var rowFont = new Font("Microsoft YaHei UI", 10);
        int maxRows = Math.Min(items.Count, 10);

        for (int i = 0; i < maxRows; i++)
        {
            var item = items[i];
            var symbol = item["symbol"]?.ToString() ?? "-";
            var smoothness = item["smoothness"]?.ToString() ?? "-";

            // 斑马纹
            if (i % 2 == 0)
            {
                using var zebraBrush = new SolidBrush(Color.FromArgb(40, 40, 48));
                g.FillRectangle(zebraBrush, margin, y, width - margin * 2, rowHeight);
            }

            // 排名颜色
            Color rankColor;
            if (i == 0) rankColor = Color.FromArgb(255, 215, 0);
            else if (i == 1) rankColor = Color.FromArgb(192, 192, 192);
            else if (i == 2) rankColor = Color.FromArgb(205, 127, 50);
            else rankColor = Color.FromArgb(180, 180, 180);

            using var rankBrush = new SolidBrush(rankColor);
            using var textBrush = new SolidBrush(Color.FromArgb(220, 220, 220));

            colX = margin + 5;
            g.DrawString($"#{i + 1}", rowFont, rankBrush, colX, y + 6);
            colX += colRankWidth;
            g.DrawString(symbol, rowFont, textBrush, colX, y + 6);
            colX += colSymbolWidth;
            g.DrawString(smoothness, rowFont, textBrush, colX, y + 6);

            y += rowHeight;
        }
    }

    private void DrawTitle(Graphics g, int width, ref int y, string sourceName)
    {
        using var titleFont = new Font("Microsoft YaHei UI", 18, FontStyle.Bold);
        using var timeFont = new Font("Microsoft YaHei UI", 10);
        using var titleBrush = new SolidBrush(Color.White);
        using var timeBrush = new SolidBrush(Color.FromArgb(180, 180, 180));

        string title = $"【{sourceName}】";
        SizeF titleSize = g.MeasureString(title, titleFont);
        float titleX = (width - titleSize.Width) / 2;

        g.DrawString(title, titleFont, titleBrush, titleX, y);

        string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        SizeF timeSize = g.MeasureString(time, timeFont);
        float timeX = (width - timeSize.Width) / 2;

        g.DrawString(time, timeFont, timeBrush, timeX, y + 28);

        y += 60;
    }

    private void DrawSection(Graphics g, int margin, int width, ref int y, string sectionTitle, JArray items, int rowHeight, int headerHeight, Color headerColor, Color headerColorDark)
    {
        int colRankWidth = 50;
        int colSymbolWidth = 100;
        int colScoreWidth = 100;
        int tableWidth = margin * 2 + colRankWidth + colSymbolWidth + colScoreWidth;
        int innerWidth = colRankWidth + colSymbolWidth + colScoreWidth;

        // 标题
        using var titleFont = new Font("Microsoft YaHei UI", 14, FontStyle.Bold);
        using var headerFont = new Font("Microsoft YaHei UI", 11, FontStyle.Bold);
        using var dataFont = new Font("Consolas", 11);
        using var titleBrush = new SolidBrush(Color.White);
        using var headerBgBrush = new LinearGradientBrush(
            new Rectangle(margin, y, tableWidth, headerHeight),
            headerColor,
            headerColorDark,
            LinearGradientMode.Vertical);
        using var headerTextBrush = new SolidBrush(Color.White);
        using var rowBgBrush = new SolidBrush(Color.FromArgb(45, 45, 50));
        using var altRowBgBrush = new SolidBrush(Color.FromArgb(38, 38, 42));
        using var borderPen = new Pen(Color.FromArgb(60, 60, 65), 1);
        using var gridPen = new Pen(Color.FromArgb(50, 50, 55), 1);

        // 绘制标题背景
        var titleRect = new Rectangle(margin, y, tableWidth, 35);
        g.FillRectangle(headerBgBrush, titleRect);

        // 绘制标题文字
        StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(sectionTitle, titleFont, titleBrush, new RectangleF(titleRect.X, titleRect.Y + 2, titleRect.Width, titleRect.Height), sf);

        y += 35;

        // 表头背景
        var headerRect = new Rectangle(margin, y, tableWidth, headerHeight);
        g.FillRectangle(new SolidBrush(Color.FromArgb(50, 50, 55)), headerRect);

        // 表头文字
        int headerY = y + (headerHeight - 20) / 2;

        g.DrawString("排名", headerFont, headerTextBrush, margin + 10, headerY);
        g.DrawString("合约代码", headerFont, headerTextBrush, margin + colRankWidth + 10, headerY);
        g.DrawString("分数", headerFont, headerTextBrush, margin + colRankWidth + colSymbolWidth + 10, headerY);

        y += headerHeight;

        // 数据行
        int displayCount = Math.Min(items.Count, 10);
        for (int i = 0; i < displayCount; i++)
        {
            var item = items[i];
            var symbol = item["symbol"]?.ToString() ?? "-";
            var score = item["score"]?.ToString() ?? "-";

            // 交替背景色
            using var rowBrush = new SolidBrush(i % 2 == 0 ? Color.FromArgb(45, 45, 50) : Color.FromArgb(38, 38, 42));
            var rowRect = new Rectangle(margin, y, tableWidth, rowHeight);
            g.FillRectangle(rowBrush, rowRect);

            // 绘制分隔线
            g.DrawLine(gridPen, margin, y + rowHeight - 1, margin + tableWidth, y + rowHeight - 1);

            int dataY = y + (rowHeight - 20) / 2;

            // 排名（带颜色）
            using var rankBrush = new SolidBrush(GetRankColor(i));
            string rankText = $"#{i + 1}";
            g.DrawString(rankText, dataFont, rankBrush, margin + 12, dataY);

            // 合约代码
            using var symbolBrush = new SolidBrush(Color.FromArgb(220, 220, 220));
            g.DrawString(symbol, dataFont, symbolBrush, margin + colRankWidth + 10, dataY);

            // 分数
            using var scoreBrush = new SolidBrush(Color.FromArgb(200, 200, 200));
            g.DrawString(score, dataFont, scoreBrush, margin + colRankWidth + colSymbolWidth + 10, dataY);

            y += rowHeight;
        }

        // 边框
        g.DrawRectangle(borderPen, margin, y - (headerHeight + displayCount * rowHeight) - 35, tableWidth, headerHeight + displayCount * rowHeight + 35);
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
        // 如果已经是友好名称，直接返回
        if (!string.IsNullOrEmpty(sourceName) && !int.TryParse(sourceName, out _))
        {
            return sourceName;
        }
        return defaultTitle;
    }

    private Color GetRankColor(int index)
    {
        return index switch
        {
            0 => Color.FromArgb(255, 215, 0),    // 金色
            1 => Color.FromArgb(192, 192, 192), // 银色
            2 => Color.FromArgb(205, 127, 50),  // 铜色
            _ => Color.FromArgb(180, 180, 180)  // 普通
        };
    }

    public string SaveImageToTempFile(byte[] imageData, string prefix = "ranking")
    {
        var fileName = $"{prefix}_{DateTime.Now:yyyyMMddHHmmss}.png";
        var filePath = Path.Combine(_tempFolder, fileName);
        File.WriteAllBytes(filePath, imageData);
        return filePath;
    }

    public void CleanupTempFiles(int maxAgeMinutes = 60)
    {
        try
        {
            if (!Directory.Exists(_tempFolder))
                return;

            var cutoffTime = DateTime.Now.AddMinutes(-maxAgeMinutes);
            var files = Directory.GetFiles(_tempFolder, "*.png");

            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < cutoffTime)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }
}
