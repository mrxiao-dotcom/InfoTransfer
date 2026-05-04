using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using InfoTransfer.Models;

namespace InfoTransfer.Services;

public class DatabaseService
{
    private readonly string _connectionString;

    public DatabaseService(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // 创建表
        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS FeishuMessages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MessageType TEXT NOT NULL,
                TerminalId TEXT NOT NULL,
                Method TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Pending',
                Body TEXT NOT NULL DEFAULT '{}',
                ReceivedAt TEXT NOT NULL,
                ProcessedAt TEXT,
                CreatedAt TEXT NOT NULL,
                SourceId TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_feishu_terminal ON FeishuMessages(TerminalId);
            CREATE INDEX IF NOT EXISTS idx_feishu_status ON FeishuMessages(Status);
            CREATE INDEX IF NOT EXISTS idx_feishu_received ON FeishuMessages(ReceivedAt);

            CREATE TABLE IF NOT EXISTS TerminalConfigs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TerminalId TEXT NOT NULL UNIQUE,
                TextWebhook TEXT,
                ImageApiKey TEXT,
                ImageSecretKey TEXT,
                ImageReceiverId TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS MessageSources (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceId TEXT NOT NULL UNIQUE,
                Name TEXT,
                ApiMethod TEXT NOT NULL DEFAULT 'GET',
                ApiUrl TEXT,
                ApiParameters TEXT,
                ResponseFormat TEXT,
                Description TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PushAccounts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AccountId TEXT NOT NULL UNIQUE,
                Name TEXT,
                AccountType TEXT NOT NULL DEFAULT 'Feishu',
                Credentials TEXT,
                Description TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PushCombinations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TerminalId TEXT NOT NULL,
                SourceId TEXT NOT NULL,
                EnableText INTEGER NOT NULL DEFAULT 1,
                EnableImage INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ScheduledTasks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TaskId TEXT NOT NULL UNIQUE,
                Name TEXT,
                SourceId TEXT NOT NULL,
                TerminalId TEXT NOT NULL,
                EnableText INTEGER NOT NULL DEFAULT 1,
                EnableImage INTEGER NOT NULL DEFAULT 0,
                ScheduleTimes TEXT NOT NULL DEFAULT '08:00,20:00',
                LastRunTime TEXT,
                ExecutedToday TEXT,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS TaskHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TaskId TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Pending',
                ErrorMessage TEXT,
                ResponseData TEXT,
                ExecutedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS GDSignalConfigs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL DEFAULT 'GD信号监控',
                ApiBaseUrl TEXT,
                ApiToken TEXT,
                Strategys TEXT,
                MonitorStartTime TEXT DEFAULT '09:00',
                MonitorEndTime TEXT DEFAULT '15:00',
                MonitorNightSession INTEGER DEFAULT 0,
                NightSessionStartTime TEXT DEFAULT '21:00',
                NightSessionEndTime TEXT DEFAULT '02:30',
                MonitorIntervalMinutes INTEGER DEFAULT 30,
                UseFixedTimePoints INTEGER DEFAULT 1,
                FixedTimeMinutes TEXT DEFAULT '0,15,30,45',
                TerminalId TEXT,
                EnableText INTEGER DEFAULT 1,
                EnableImage INTEGER DEFAULT 0,
                IsEnabled INTEGER DEFAULT 0,
                Conditions TEXT DEFAULT '[]',
                EnableGD15 INTEGER DEFAULT 0,
                EnableGD20 INTEGER DEFAULT 0,
                EnableGD25 INTEGER DEFAULT 0,
                EnableGD30 INTEGER DEFAULT 0,
                EnableGD35 INTEGER DEFAULT 0,
                EnableGD40 INTEGER DEFAULT 0,
                EnableRealTimeStopPriceDiffRateCondition INTEGER DEFAULT 1,
                EnableRemainingRiskCondition INTEGER DEFAULT 0,
                RealTimeStopPriceDiffRateValue REAL DEFAULT 0,
                RemainingRiskValue REAL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            PRAGMA journal_mode=WAL;
        ";
        command.ExecuteNonQuery();

        // 执行数据库迁移
        MigrateBodyColumn();
        MigrateScheduledTasksTable(connection);
        MigratePushCombinationsTable(connection);
        MigrateMessageSourcesApiToken(connection);
        MigrateGDSignalConfigFields(connection);

        // 初始化默认消息源
        InitializeDefaultMessageSources(connection);
        MigrateMessageSources(connection);
    }

    private void MigrateMessageSourcesApiToken(SqliteConnection connection)
    {
        try
        {
            // 检查列是否存在
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info(MessageSources)";
            var reader = checkCmd.ExecuteReader();
            bool hasTokenColumn = false;
            while (reader.Read())
            {
                if (reader.GetString(1) == "ApiToken")
                {
                    hasTokenColumn = true;
                    break;
                }
            }
            reader.Close();

            if (!hasTokenColumn)
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE MessageSources ADD COLUMN ApiToken TEXT";
                alterCmd.ExecuteNonQuery();
                System.Diagnostics.Debug.WriteLine("已添加 MessageSources.ApiToken 列");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"迁移 ApiToken 列失败: {ex.Message}");
        }
    }

    private void MigrateGDSignalConfigFields(SqliteConnection connection)
    {
        try
        {
            var newColumns = new[]
            {
                ("EnableGD15", "INTEGER DEFAULT 0"),
                ("EnableGD20", "INTEGER DEFAULT 0"),
                ("EnableGD25", "INTEGER DEFAULT 0"),
                ("EnableGD30", "INTEGER DEFAULT 0"),
                ("EnableGD35", "INTEGER DEFAULT 0"),
                ("EnableGD40", "INTEGER DEFAULT 0"),
                ("EnableRealTimeStopPriceDiffRateCondition", "INTEGER DEFAULT 1"),
                ("EnableRemainingRiskCondition", "INTEGER DEFAULT 0"),
                ("RealTimeStopPriceDiffRateValue", "REAL DEFAULT 0"),
                ("RemainingRiskValue", "REAL DEFAULT 0"),
                ("FixedTimeMinutes", "TEXT DEFAULT '0,15,30,45'"),
                ("NightSessionStartTime", "TEXT DEFAULT '21:00'"),
                ("NightSessionEndTime", "TEXT DEFAULT '02:30'")
            };

            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info(GDSignalConfigs)";
            var reader = checkCmd.ExecuteReader();
            var existingColumns = new HashSet<string>();
            while (reader.Read())
            {
                existingColumns.Add(reader.GetString(1));
            }
            reader.Close();

            foreach (var (columnName, defaultValue) in newColumns)
            {
                if (!existingColumns.Contains(columnName))
                {
                    using var alterCmd = connection.CreateCommand();
                    alterCmd.CommandText = $"ALTER TABLE GDSignalConfigs ADD COLUMN {columnName} {defaultValue}";
                    alterCmd.ExecuteNonQuery();
                    System.Diagnostics.Debug.WriteLine($"已添加 GDSignalConfigs.{columnName} 列");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"迁移 GDSignalConfigs 字段失败: {ex.Message}");
        }
    }

    private void MigrateMessageSources(SqliteConnection connection)
    {
        try
        {
            var now = DateTime.Now.ToString("o");
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var apiToken = "015160f03bc7451d8c084aba3bf7355e";

            // 更新流畅度排名消息源的参数和 Token
            using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText = @"
                UPDATE MessageSources 
                SET ApiParameters = $apiParameters, ApiToken = $apiToken, UpdatedAt = $updatedAt
                WHERE SourceId = '2'
            ";
            updateCmd.Parameters.AddWithValue("$apiParameters", $"date={today}");
            updateCmd.Parameters.AddWithValue("$apiToken", apiToken);
            updateCmd.Parameters.AddWithValue("$updatedAt", now);
            var rows = updateCmd.ExecuteNonQuery();

            if (rows == 0)
            {
                System.Diagnostics.Debug.WriteLine("未找到消息源 2，跳过迁移");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"已更新消息源 2 的 Token");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"迁移消息源配置错误: {ex.Message}");
        }
    }

    private void InitializeDefaultMessageSources(SqliteConnection connection)
    {
        try
        {
            var existingSources = new HashSet<string>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT SourceId FROM MessageSources";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existingSources.Add(reader.GetString(0));
            }
            reader.Close();

            var now = DateTime.Now.ToString("o");
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var defaultSources = new List<(string sourceId, string name, string apiMethod, string apiUrl, string apiParameters, string responseFormat, string description)>
            {
                ("1", "多空排名", "GET", "http://82.157.17.88:5000/rankings", "", "multi_short_ranking", "获取期货多空排名数据"),
                ("2", "流畅度排名", "GET", "http://82.157.17.88:5050/api/ranking/fluency/date/{date}", $"date={today}", "fluency_ranking", "获取期货流畅度排名数据")
            };

            foreach (var source in defaultSources)
            {
                if (!existingSources.Contains(source.sourceId))
                {
                    using var insertCmd = connection.CreateCommand();
                    insertCmd.CommandText = @"
                        INSERT INTO MessageSources (SourceId, Name, ApiMethod, ApiUrl, ApiParameters, ResponseFormat, Description, CreatedAt, UpdatedAt)
                        VALUES ($sourceId, $name, $apiMethod, $apiUrl, $apiParameters, $responseFormat, $description, $createdAt, $updatedAt)
                    ";
                    insertCmd.Parameters.AddWithValue("$sourceId", source.sourceId);
                    insertCmd.Parameters.AddWithValue("$name", source.name);
                    insertCmd.Parameters.AddWithValue("$apiMethod", source.apiMethod);
                    insertCmd.Parameters.AddWithValue("$apiUrl", source.apiUrl);
                    insertCmd.Parameters.AddWithValue("$apiParameters", source.apiParameters);
                    insertCmd.Parameters.AddWithValue("$responseFormat", source.responseFormat);
                    insertCmd.Parameters.AddWithValue("$description", source.description);
                    insertCmd.Parameters.AddWithValue("$createdAt", now);
                    insertCmd.Parameters.AddWithValue("$updatedAt", now);
                    insertCmd.ExecuteNonQuery();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"初始化默认消息源错误: {ex.Message}");
        }
    }

    private void MigratePushCombinationsTable(SqliteConnection connection)
    {
        try
        {
            var columns = new HashSet<string>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(PushCombinations)";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
            reader.Close();

            if (!columns.Contains("TerminalId"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE PushCombinations ADD COLUMN TerminalId TEXT NOT NULL DEFAULT ''";
                alterCmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PushCombinations 迁移错误: {ex.Message}");
        }
    }

    private void MigrateScheduledTasksTable(SqliteConnection connection)
    {
        try
        {
            // 检查现有表的列
            var columns = new HashSet<string>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(ScheduledTasks)";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }

            // 如果没有 ScheduleTimes 列，添加它
            if (!columns.Contains("ScheduleTimes"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE ScheduledTasks ADD COLUMN ScheduleTimes TEXT NOT NULL DEFAULT '08:00,20:00'";
                alterCmd.ExecuteNonQuery();
            }

            // 如果没有 ExecutedToday 列，添加它
            if (!columns.Contains("ExecutedToday"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE ScheduledTasks ADD COLUMN ExecutedToday TEXT";
                alterCmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ScheduledTasks 迁移错误: {ex.Message}");
        }
    }

    private void MigrateBodyColumn()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var checkCommand = connection.CreateCommand();
            checkCommand.CommandText = "PRAGMA table_info(FeishuMessages)";
            var reader = checkCommand.ExecuteReader();

            var columns = new HashSet<string>();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
            reader.Close();

            if (!columns.Contains("Body"))
            {
                var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE FeishuMessages ADD COLUMN Body TEXT NOT NULL DEFAULT '{}'";
                alterCommand.ExecuteNonQuery();
            }

            if (!columns.Contains("SourceId"))
            {
                var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE FeishuMessages ADD COLUMN SourceId TEXT";
                alterCommand.ExecuteNonQuery();
            }
        }
        catch
        {
            // 忽略迁移错误
        }
    }

    public int CreateMessage(FeishuMessage message)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO FeishuMessages (MessageType, TerminalId, Method, Status, Body, ReceivedAt, CreatedAt, SourceId)
            VALUES ($messageType, $terminalId, $method, $status, $body, $receivedAt, $createdAt, $sourceId);
            SELECT last_insert_rowid();
        ";
        command.Parameters.AddWithValue("$messageType", message.MessageType);
        command.Parameters.AddWithValue("$terminalId", message.TerminalId);
        command.Parameters.AddWithValue("$method", message.Method);
        command.Parameters.AddWithValue("$status", message.Status);
        command.Parameters.AddWithValue("$body", message.Body ?? "{}");
        command.Parameters.AddWithValue("$receivedAt", message.ReceivedAt);
        command.Parameters.AddWithValue("$createdAt", message.CreatedAt);
        command.Parameters.AddWithValue("$sourceId", message.SourceId ?? (object)DBNull.Value);

        return Convert.ToInt32(command.ExecuteScalar());
    }

    public List<FeishuMessage> GetMessagesByStatus(string status, int limit = 50)
    {
        var messages = new List<FeishuMessage>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        
        // 对于 Pending 状态的消息，只获取30分钟内的（时效性已过的消息不再推送）
        string whereClause = "Status = $status";
        if (status == "Pending")
        {
            var expireTime = DateTime.Now.AddMinutes(-30).ToString("yyyy-MM-dd HH:mm:ss");
            whereClause += " AND CreatedAt >= $expireTime";
            command.Parameters.AddWithValue("$expireTime", expireTime);
        }

        command.CommandText = $@"
            SELECT Id, MessageType, TerminalId, Method, Status, Body, ReceivedAt, ProcessedAt, CreatedAt, SourceId
            FROM FeishuMessages
            WHERE {whereClause}
            ORDER BY CreatedAt ASC
            LIMIT $limit
        ";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            messages.Add(new FeishuMessage
            {
                Id = reader.GetInt32(0),
                MessageType = reader.GetString(1),
                TerminalId = reader.GetString(2),
                Method = reader.GetString(3),
                Status = reader.GetString(4),
                Body = reader.IsDBNull(5) ? "{}" : reader.GetString(5),
                ReceivedAt = reader.GetString(6),
                ProcessedAt = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt = reader.GetString(8),
                SourceId = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }

        return messages;
    }

    /// <summary>
    /// 清理超过30分钟未推送的消息（时效性已过，标记为过期）
    /// </summary>
    public int CleanupExpiredMessages()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var expireTime = DateTime.Now.AddMinutes(-30).ToString("yyyy-MM-dd HH:mm:ss");
        
        var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE FeishuMessages
            SET Status = 'Expired', ProcessedAt = $processedAt
            WHERE Status = 'Pending' AND CreatedAt < $expireTime
        ";
        command.Parameters.AddWithValue("$expireTime", expireTime);
        command.Parameters.AddWithValue("$processedAt", DateTime.Now.ToString("o"));

        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// 删除前一天的所有消息记录
    /// </summary>
    public int CleanupYesterdayMessages()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var today = DateTime.Today.ToString("yyyy-MM-dd");

        var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM FeishuMessages
            WHERE date(CreatedAt) = $yesterday
        ";
        command.Parameters.AddWithValue("$yesterday", yesterday);

        return command.ExecuteNonQuery();
    }

    public void UpdateMessageStatus(int id, string status, DateTime processedAt)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE FeishuMessages
            SET Status = $status, ProcessedAt = $processedAt
            WHERE Id = $id
        ";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$processedAt", processedAt.ToString("o"));
        command.Parameters.AddWithValue("$id", id);

        command.ExecuteNonQuery();
    }

    public List<FeishuMessage> GetRecentMessages(int limit = 100)
    {
        var messages = new List<FeishuMessage>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, MessageType, TerminalId, Method, Status, Body, ReceivedAt, ProcessedAt, CreatedAt
            FROM FeishuMessages
            ORDER BY CreatedAt DESC
            LIMIT $limit
        ";
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            messages.Add(new FeishuMessage
            {
                Id = reader.GetInt32(0),
                MessageType = reader.GetString(1),
                TerminalId = reader.GetString(2),
                Method = reader.GetString(3),
                Status = reader.GetString(4),
                Body = reader.IsDBNull(5) ? "{}" : reader.GetString(5),
                ReceivedAt = reader.GetString(6),
                ProcessedAt = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt = reader.GetString(8)
            });
        }

        return messages;
    }

    public (int Pending, int Sent, int Failed) GetMessageCounts()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Status, COUNT(*) as Count FROM FeishuMessages GROUP BY Status
        ";

        int pending = 0, sent = 0, failed = 0;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var status = reader.GetString(0);
            var count = reader.GetInt32(1);

            switch (status)
            {
                case "Pending": pending = count; break;
                case "Sent": sent = count; break;
                case "Failed": failed = count; break;
            }
        }

        return (pending, sent, failed);
    }

    public void ClearAllMessages()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM FeishuMessages";
        command.ExecuteNonQuery();
    }

    public void BackupTo(string backupPath)
    {
        using var sourceConnection = new SqliteConnection(_connectionString);
        sourceConnection.Open();

        using var backupConnection = new SqliteConnection($"Data Source={backupPath}");
        backupConnection.Open();

        sourceConnection.BackupDatabase(backupConnection);
    }

    public void RestoreFrom(string backupPath)
    {
        using var restoreConnection = new SqliteConnection($"Data Source={backupPath}");
        restoreConnection.Open();

        using var targetConnection = new SqliteConnection(_connectionString);
        targetConnection.Open();

        restoreConnection.BackupDatabase(targetConnection);
    }

    public FeishuMessage? GetMessageById(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, MessageType, TerminalId, Method, Status, Body, ReceivedAt, ProcessedAt, CreatedAt
            FROM FeishuMessages
            WHERE Id = $id
        ";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return new FeishuMessage
            {
                Id = reader.GetInt32(0),
                MessageType = reader.GetString(1),
                TerminalId = reader.GetString(2),
                Method = reader.GetString(3),
                Status = reader.GetString(4),
                Body = reader.IsDBNull(5) ? "{}" : reader.GetString(5),
                ReceivedAt = reader.GetString(6),
                ProcessedAt = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt = reader.GetString(8)
            };
        }

        return null;
    }

    public void UpdateMessage(FeishuMessage message)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE FeishuMessages
            SET TerminalId = $terminalId,
                Method = $method,
                Status = $status,
                Body = $body
            WHERE Id = $id
        ";
        command.Parameters.AddWithValue("$terminalId", message.TerminalId);
        command.Parameters.AddWithValue("$method", message.Method);
        command.Parameters.AddWithValue("$status", message.Status);
        command.Parameters.AddWithValue("$body", message.Body ?? "{}");
        command.Parameters.AddWithValue("$id", message.Id);

        command.ExecuteNonQuery();
    }

    public void DeleteMessage(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM FeishuMessages WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // 终端配置相关方法
    public List<TerminalConfig> GetAllTerminalConfigs()
    {
        var configs = new List<TerminalConfig>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, TerminalId, TextWebhook, ImageApiKey, ImageSecretKey, ImageReceiverId
            FROM TerminalConfigs
            ORDER BY CreatedAt ASC
        ";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            configs.Add(new TerminalConfig
            {
                Id = reader.GetInt32(0),
                TerminalId = reader.GetString(1),
                TextWebhook = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ImageApiKey = reader.IsDBNull(3) ? "" : reader.GetString(3),
                ImageSecretKey = reader.IsDBNull(4) ? "" : reader.GetString(4),
                ImageReceiverId = reader.IsDBNull(5) ? "" : reader.GetString(5)
            });
        }

        return configs;
    }

    public void SaveTerminalConfig(TerminalConfig config)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var now = DateTime.Now.ToString("o");

        var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT Id FROM TerminalConfigs WHERE TerminalId = $terminalId";
        checkCommand.Parameters.AddWithValue("$terminalId", config.TerminalId);
        var exists = checkCommand.ExecuteScalar();

        if (exists != null)
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE TerminalConfigs
                SET TextWebhook = $textWebhook,
                    ImageApiKey = $imageApiKey,
                    ImageSecretKey = $imageSecretKey,
                    ImageReceiverId = $imageReceiverId,
                    UpdatedAt = $updatedAt
                WHERE TerminalId = $terminalId
            ";
            command.Parameters.AddWithValue("$textWebhook", config.TextWebhook ?? "");
            command.Parameters.AddWithValue("$imageApiKey", config.ImageApiKey ?? "");
            command.Parameters.AddWithValue("$imageSecretKey", config.ImageSecretKey ?? "");
            command.Parameters.AddWithValue("$imageReceiverId", config.ImageReceiverId ?? "");
            command.Parameters.AddWithValue("$updatedAt", now);
            command.Parameters.AddWithValue("$terminalId", config.TerminalId);
            command.ExecuteNonQuery();
        }
        else
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO TerminalConfigs (TerminalId, TextWebhook, ImageApiKey, ImageSecretKey, ImageReceiverId, CreatedAt, UpdatedAt)
                VALUES ($terminalId, $textWebhook, $imageApiKey, $imageSecretKey, $imageReceiverId, $createdAt, $updatedAt)
            ";
            command.Parameters.AddWithValue("$terminalId", config.TerminalId);
            command.Parameters.AddWithValue("$textWebhook", config.TextWebhook ?? "");
            command.Parameters.AddWithValue("$imageApiKey", config.ImageApiKey ?? "");
            command.Parameters.AddWithValue("$imageSecretKey", config.ImageSecretKey ?? "");
            command.Parameters.AddWithValue("$imageReceiverId", config.ImageReceiverId ?? "");
            command.Parameters.AddWithValue("$createdAt", now);
            command.Parameters.AddWithValue("$updatedAt", now);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteTerminalConfig(string terminalId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM TerminalConfigs WHERE TerminalId = $terminalId";
        command.Parameters.AddWithValue("$terminalId", terminalId);
        command.ExecuteNonQuery();
    }

    public void SaveAllTerminalConfigs(List<TerminalConfig> configs)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();

        var deleteCommand = connection.CreateCommand();
        deleteCommand.CommandText = "DELETE FROM TerminalConfigs";
        deleteCommand.ExecuteNonQuery();

        var now = DateTime.Now.ToString("o");
        foreach (var config in configs)
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO TerminalConfigs (TerminalId, TextWebhook, ImageApiKey, ImageSecretKey, ImageReceiverId, CreatedAt, UpdatedAt)
                VALUES ($terminalId, $textWebhook, $imageApiKey, $imageSecretKey, $imageReceiverId, $createdAt, $updatedAt)
            ";
            command.Parameters.AddWithValue("$terminalId", config.TerminalId);
            command.Parameters.AddWithValue("$textWebhook", config.TextWebhook ?? "");
            command.Parameters.AddWithValue("$imageApiKey", config.ImageApiKey ?? "");
            command.Parameters.AddWithValue("$imageSecretKey", config.ImageSecretKey ?? "");
            command.Parameters.AddWithValue("$imageReceiverId", config.ImageReceiverId ?? "");
            command.Parameters.AddWithValue("$createdAt", now);
            command.Parameters.AddWithValue("$updatedAt", now);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // 消息源相关方法
    public List<MessageSource> GetAllMessageSources()
    {
        var sources = new List<MessageSource>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, SourceId, Name, ApiMethod, ApiUrl, ApiParameters, ResponseFormat, Description
            FROM MessageSources
            ORDER BY CreatedAt ASC
        ";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sources.Add(new MessageSource
            {
                Id = reader.GetInt32(0),
                SourceId = reader.GetString(1),
                Name = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ApiMethod = reader.IsDBNull(3) ? "GET" : reader.GetString(3),
                ApiUrl = reader.IsDBNull(4) ? "" : reader.GetString(4),
                ApiParameters = reader.IsDBNull(5) ? "" : reader.GetString(5),
                ResponseFormat = reader.IsDBNull(6) ? "" : reader.GetString(6),
                Description = reader.IsDBNull(7) ? "" : reader.GetString(7)
            });
        }

        return sources;
    }

    public MessageSource? GetMessageSourceByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, SourceId, Name, ApiMethod, ApiUrl, ApiParameters, ResponseFormat, Description, ApiToken
            FROM MessageSources
            WHERE Name = $name OR SourceId = $name
            LIMIT 1
        ";
        command.Parameters.AddWithValue("$name", name);

        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return new MessageSource
            {
                Id = reader.GetInt32(0),
                SourceId = reader.GetString(1),
                Name = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ApiMethod = reader.IsDBNull(3) ? "GET" : reader.GetString(3),
                ApiUrl = reader.IsDBNull(4) ? "" : reader.GetString(4),
                ApiParameters = reader.IsDBNull(5) ? "" : reader.GetString(5),
                ResponseFormat = reader.IsDBNull(6) ? "" : reader.GetString(6),
                Description = reader.IsDBNull(7) ? "" : reader.GetString(7),
                ApiToken = reader.IsDBNull(8) ? "" : reader.GetString(8)
            };
        }

        return null;
    }

    public MessageSource? GetMessageSourceBySourceId(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return null;

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, SourceId, Name, ApiMethod, ApiUrl, ApiParameters, ResponseFormat, Description, ApiToken
            FROM MessageSources
            WHERE SourceId = $sourceId
            LIMIT 1
        ";
        command.Parameters.AddWithValue("$sourceId", sourceId);

        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return new MessageSource
            {
                Id = reader.GetInt32(0),
                SourceId = reader.GetString(1),
                Name = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ApiMethod = reader.IsDBNull(3) ? "GET" : reader.GetString(3),
                ApiUrl = reader.IsDBNull(4) ? "" : reader.GetString(4),
                ApiParameters = reader.IsDBNull(5) ? "" : reader.GetString(5),
                ResponseFormat = reader.IsDBNull(6) ? "" : reader.GetString(6),
                Description = reader.IsDBNull(7) ? "" : reader.GetString(7),
                ApiToken = reader.IsDBNull(8) ? "" : reader.GetString(8)
            };
        }

        return null;
    }

    public void SaveMessageSource(MessageSource source)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var now = DateTime.Now.ToString("o");

        if (source.Id > 0)
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE MessageSources
                SET SourceId = $sourceId, Name = $name, ApiMethod = $apiMethod,
                    ApiUrl = $apiUrl, ApiParameters = $apiParameters,
                    ResponseFormat = $responseFormat, Description = $description,
                    ApiToken = $apiToken, UpdatedAt = $updatedAt
                WHERE Id = $id
            ";
            command.Parameters.AddWithValue("$id", source.Id);
            command.Parameters.AddWithValue("$sourceId", source.SourceId);
            command.Parameters.AddWithValue("$name", source.Name ?? "");
            command.Parameters.AddWithValue("$apiMethod", source.ApiMethod);
            command.Parameters.AddWithValue("$apiUrl", source.ApiUrl ?? "");
            command.Parameters.AddWithValue("$apiParameters", source.ApiParameters ?? "");
            command.Parameters.AddWithValue("$responseFormat", source.ResponseFormat ?? "");
            command.Parameters.AddWithValue("$description", source.Description ?? "");
            command.Parameters.AddWithValue("$apiToken", source.ApiToken ?? "");
            command.Parameters.AddWithValue("$updatedAt", now);
            command.ExecuteNonQuery();
        }
        else
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO MessageSources (SourceId, Name, ApiMethod, ApiUrl, ApiParameters, ResponseFormat, Description, ApiToken, CreatedAt, UpdatedAt)
                VALUES ($sourceId, $name, $apiMethod, $apiUrl, $apiParameters, $responseFormat, $description, $apiToken, $createdAt, $updatedAt)
            ";
            command.Parameters.AddWithValue("$sourceId", source.SourceId);
            command.Parameters.AddWithValue("$name", source.Name ?? "");
            command.Parameters.AddWithValue("$apiMethod", source.ApiMethod);
            command.Parameters.AddWithValue("$apiUrl", source.ApiUrl ?? "");
            command.Parameters.AddWithValue("$apiParameters", source.ApiParameters ?? "");
            command.Parameters.AddWithValue("$responseFormat", source.ResponseFormat ?? "");
            command.Parameters.AddWithValue("$description", source.Description ?? "");
            command.Parameters.AddWithValue("$apiToken", source.ApiToken ?? "");
            command.Parameters.AddWithValue("$createdAt", now);
            command.Parameters.AddWithValue("$updatedAt", now);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteMessageSource(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM MessageSources WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // 推送账号相关方法
    public List<PushAccount> GetAllPushAccounts()
    {
        var accounts = new List<PushAccount>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, AccountId, Name, AccountType, Credentials, Description
            FROM PushAccounts
            ORDER BY CreatedAt ASC
        ";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            accounts.Add(new PushAccount
            {
                Id = reader.GetInt32(0),
                AccountId = reader.GetString(1),
                Name = reader.IsDBNull(2) ? "" : reader.GetString(2),
                AccountType = reader.IsDBNull(3) ? "Feishu" : reader.GetString(3),
                Credentials = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Description = reader.IsDBNull(5) ? "" : reader.GetString(5)
            });
        }

        return accounts;
    }

    public void SavePushAccount(PushAccount account)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var now = DateTime.Now.ToString("o");

        if (account.Id > 0)
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE PushAccounts
                SET AccountId = $accountId, Name = $name, AccountType = $accountType,
                    Credentials = $credentials, Description = $description,
                    UpdatedAt = $updatedAt
                WHERE Id = $id
            ";
            command.Parameters.AddWithValue("$id", account.Id);
            command.Parameters.AddWithValue("$accountId", account.AccountId);
            command.Parameters.AddWithValue("$name", account.Name ?? "");
            command.Parameters.AddWithValue("$accountType", account.AccountType);
            command.Parameters.AddWithValue("$credentials", account.Credentials ?? "");
            command.Parameters.AddWithValue("$description", account.Description ?? "");
            command.Parameters.AddWithValue("$updatedAt", now);
            command.ExecuteNonQuery();
        }
        else
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO PushAccounts (AccountId, Name, AccountType, Credentials, Description, CreatedAt, UpdatedAt)
                VALUES ($accountId, $name, $accountType, $credentials, $description, $createdAt, $updatedAt)
            ";
            command.Parameters.AddWithValue("$accountId", account.AccountId);
            command.Parameters.AddWithValue("$name", account.Name ?? "");
            command.Parameters.AddWithValue("$accountType", account.AccountType);
            command.Parameters.AddWithValue("$credentials", account.Credentials ?? "");
            command.Parameters.AddWithValue("$description", account.Description ?? "");
            command.Parameters.AddWithValue("$createdAt", now);
            command.Parameters.AddWithValue("$updatedAt", now);
            command.ExecuteNonQuery();
        }
    }

    public void DeletePushAccount(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PushAccounts WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // 推送组合相关方法
    public List<PushCombination> GetAllPushCombinations()
    {
        var combinations = new List<PushCombination>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, TerminalId, SourceId, EnableText, EnableImage
            FROM PushCombinations
            ORDER BY CreatedAt ASC
        ";

        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                combinations.Add(new PushCombination
                {
                    Id = reader.GetInt32(0),
                    TerminalId = reader.GetString(1),
                    SourceId = reader.GetString(2),
                    EnableText = reader.GetInt32(3) == 1,
                    EnableImage = reader.GetInt32(4) == 1
                });
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // 表结构问题，尝试修复
            RepairPushCombinationsTable(connection);
            // 重试查询
            return GetAllPushCombinations();
        }

        return combinations;
    }

    private void RepairPushCombinationsTable(SqliteConnection connection)
    {
        try
        {
            var columns = new HashSet<string>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(PushCombinations)";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
            reader.Close();

            if (!columns.Contains("TerminalId"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE PushCombinations ADD COLUMN TerminalId TEXT NOT NULL DEFAULT ''";
                alterCmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"修复 PushCombinations 表错误: {ex.Message}");
        }
    }

    public void SavePushCombination(PushCombination combination)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var now = DateTime.Now.ToString("o");

        var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT Id FROM PushCombinations WHERE TerminalId = $terminalId AND SourceId = $sourceId";
        checkCommand.Parameters.AddWithValue("$terminalId", combination.TerminalId);
        checkCommand.Parameters.AddWithValue("$sourceId", combination.SourceId);
        var exists = checkCommand.ExecuteScalar();

        if (exists != null)
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE PushCombinations
                SET EnableText = $enableText, EnableImage = $enableImage
                WHERE TerminalId = $terminalId AND SourceId = $sourceId
            ";
            command.Parameters.AddWithValue("$enableText", combination.EnableText ? 1 : 0);
            command.Parameters.AddWithValue("$enableImage", combination.EnableImage ? 1 : 0);
            command.Parameters.AddWithValue("$terminalId", combination.TerminalId);
            command.Parameters.AddWithValue("$sourceId", combination.SourceId);
            command.ExecuteNonQuery();
        }
        else
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO PushCombinations (TerminalId, SourceId, EnableText, EnableImage, CreatedAt)
                VALUES ($terminalId, $sourceId, $enableText, $enableImage, $createdAt)
            ";
            command.Parameters.AddWithValue("$terminalId", combination.TerminalId);
            command.Parameters.AddWithValue("$sourceId", combination.SourceId);
            command.Parameters.AddWithValue("$enableText", combination.EnableText ? 1 : 0);
            command.Parameters.AddWithValue("$enableImage", combination.EnableImage ? 1 : 0);
            command.Parameters.AddWithValue("$createdAt", now);
            command.ExecuteNonQuery();
        }
    }

    public void DeletePushCombination(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PushCombinations WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // 定时任务相关方法
    public List<ScheduledTask> GetAllScheduledTasks()
    {
        var tasks = new List<ScheduledTask>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, TaskId, Name, SourceId, TerminalId, EnableText, EnableImage,
                   ScheduleTimes, LastRunTime, ExecutedToday, IsEnabled, CreatedAt, UpdatedAt
            FROM ScheduledTasks
            ORDER BY CreatedAt ASC
        ";

        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tasks.Add(new ScheduledTask
                {
                    Id = reader.GetInt32(0),
                    TaskId = reader.GetString(1),
                    Name = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    SourceId = reader.GetString(3),
                    TerminalId = reader.GetString(4),
                    EnableText = reader.GetInt32(5) == 1,
                    EnableImage = reader.GetInt32(6) == 1,
                    ScheduleTimes = reader.IsDBNull(7) ? "08:00,20:00" : reader.GetString(7),
                    LastRunTime = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8)),
                    ExecutedToday = reader.IsDBNull(9) ? "" : reader.GetString(9),
                    IsEnabled = reader.GetInt32(10) == 1,
                    CreatedAt = DateTime.Parse(reader.GetString(11)),
                    UpdatedAt = DateTime.Parse(reader.GetString(12))
                });
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // 表结构问题，尝试修复
            RepairScheduledTasksTable(connection);
            // 重试查询
            return GetAllScheduledTasks();
        }

        return tasks;
    }

    private void RepairScheduledTasksTable(SqliteConnection connection)
    {
        try
        {
            // 检查当前表结构
            var columns = new HashSet<string>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(ScheduledTasks)";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
            reader.Close();

            // 添加缺失的列
            if (!columns.Contains("ScheduleTimes"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE ScheduledTasks ADD COLUMN ScheduleTimes TEXT NOT NULL DEFAULT '08:00,20:00'";
                alterCmd.ExecuteNonQuery();
            }

            if (!columns.Contains("ExecutedToday"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE ScheduledTasks ADD COLUMN ExecutedToday TEXT";
                alterCmd.ExecuteNonQuery();
            }

            if (!columns.Contains("TerminalId"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE ScheduledTasks ADD COLUMN TerminalId TEXT NOT NULL DEFAULT ''";
                alterCmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"修复表错误: {ex.Message}");
        }
    }

    public List<ScheduledTask> GetEnabledScheduledTasks()
    {
        var tasks = new List<ScheduledTask>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, TaskId, Name, SourceId, TerminalId, EnableText, EnableImage,
                   ScheduleTimes, LastRunTime, ExecutedToday, IsEnabled, CreatedAt, UpdatedAt
            FROM ScheduledTasks
            WHERE IsEnabled = 1
            ORDER BY CreatedAt ASC
        ";

        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tasks.Add(new ScheduledTask
                {
                    Id = reader.GetInt32(0),
                    TaskId = reader.GetString(1),
                    Name = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    SourceId = reader.GetString(3),
                    TerminalId = reader.GetString(4),
                    EnableText = reader.GetInt32(5) == 1,
                    EnableImage = reader.GetInt32(6) == 1,
                    ScheduleTimes = reader.IsDBNull(7) ? "08:00,20:00" : reader.GetString(7),
                    LastRunTime = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8)),
                    ExecutedToday = reader.IsDBNull(9) ? "" : reader.GetString(9),
                    IsEnabled = reader.GetInt32(10) == 1,
                    CreatedAt = DateTime.Parse(reader.GetString(11)),
                    UpdatedAt = DateTime.Parse(reader.GetString(12))
                });
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // 表结构问题，尝试修复
            RepairScheduledTasksTable(connection);
            // 重试查询
            return GetEnabledScheduledTasks();
        }

        return tasks;
    }

    public void SaveScheduledTask(ScheduledTask task)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var now = DateTime.Now.ToString("o");

        if (task.Id > 0)
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE ScheduledTasks
                SET TaskId = $taskId, Name = $name, SourceId = $sourceId,
                    TerminalId = $terminalId, EnableText = $enableText, EnableImage = $enableImage,
                    ScheduleTimes = $scheduleTimes, LastRunTime = $lastRunTime, ExecutedToday = $executedToday,
                    IsEnabled = $isEnabled, UpdatedAt = $updatedAt
                WHERE Id = $id
            ";
            command.Parameters.AddWithValue("$id", task.Id);
            command.Parameters.AddWithValue("$taskId", task.TaskId);
            command.Parameters.AddWithValue("$name", task.Name ?? "");
            command.Parameters.AddWithValue("$sourceId", task.SourceId);
            command.Parameters.AddWithValue("$terminalId", task.TerminalId);
            command.Parameters.AddWithValue("$enableText", task.EnableText ? 1 : 0);
            command.Parameters.AddWithValue("$enableImage", task.EnableImage ? 1 : 0);
            command.Parameters.AddWithValue("$scheduleTimes", task.ScheduleTimes ?? "08:00,20:00");
            command.Parameters.AddWithValue("$lastRunTime", task.LastRunTime?.ToString("o") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$executedToday", task.ExecutedToday ?? "");
            command.Parameters.AddWithValue("$isEnabled", task.IsEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$updatedAt", now);
            command.ExecuteNonQuery();
        }
        else
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO ScheduledTasks (TaskId, Name, SourceId, TerminalId, EnableText, EnableImage,
                    ScheduleTimes, LastRunTime, ExecutedToday, IsEnabled, CreatedAt, UpdatedAt)
                VALUES ($taskId, $name, $sourceId, $terminalId, $enableText, $enableImage,
                    $scheduleTimes, $lastRunTime, $executedToday, $isEnabled, $createdAt, $updatedAt)
            ";
            command.Parameters.AddWithValue("$taskId", task.TaskId);
            command.Parameters.AddWithValue("$name", task.Name ?? "");
            command.Parameters.AddWithValue("$sourceId", task.SourceId);
            command.Parameters.AddWithValue("$terminalId", task.TerminalId);
            command.Parameters.AddWithValue("$enableText", task.EnableText ? 1 : 0);
            command.Parameters.AddWithValue("$enableImage", task.EnableImage ? 1 : 0);
            command.Parameters.AddWithValue("$scheduleTimes", task.ScheduleTimes ?? "08:00,20:00");
            command.Parameters.AddWithValue("$lastRunTime", task.LastRunTime?.ToString("o") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$executedToday", task.ExecutedToday ?? "");
            command.Parameters.AddWithValue("$isEnabled", task.IsEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$createdAt", now);
            command.Parameters.AddWithValue("$updatedAt", now);
            command.ExecuteNonQuery();
        }
    }

    public void UpdateTaskLastRunTime(int taskId, DateTime lastRunTime)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE ScheduledTasks SET LastRunTime = $lastRunTime WHERE Id = $id";
        command.Parameters.AddWithValue("$lastRunTime", lastRunTime.ToString("o"));
        command.Parameters.AddWithValue("$id", taskId);
        command.ExecuteNonQuery();
    }

    public void DeleteScheduledTask(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ScheduledTasks WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void SaveTaskHistory(TaskHistory history)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO TaskHistory (TaskId, Status, ErrorMessage, ResponseData, ExecutedAt)
            VALUES ($taskId, $status, $errorMessage, $responseData, $executedAt)
        ";
        command.Parameters.AddWithValue("$taskId", history.TaskIdStr);
        command.Parameters.AddWithValue("$status", history.Status);
        command.Parameters.AddWithValue("$errorMessage", history.ErrorMessage ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$responseData", history.ResponseData ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$executedAt", history.ExecutedAt.ToString("o"));
        command.ExecuteNonQuery();
    }

    public List<TaskHistory> GetTaskHistory(string taskId, int limit = 50)
    {
        var histories = new List<TaskHistory>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, TaskId, Status, ErrorMessage, ResponseData, ExecutedAt
            FROM TaskHistory
            WHERE TaskId = $taskId
            ORDER BY ExecutedAt DESC
            LIMIT $limit
        ";
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            histories.Add(new TaskHistory
            {
                Id = reader.GetInt32(0),
                TaskIdStr = reader.GetString(1),
                Status = reader.GetString(2),
                ErrorMessage = reader.IsDBNull(3) ? null : reader.GetString(3),
                ResponseData = reader.IsDBNull(4) ? null : reader.GetString(4),
                ExecutedAt = DateTime.Parse(reader.GetString(5))
            });
        }

        return histories;
    }

    // GD信号监控配置相关方法
    public List<GDSignalConfig> GetAllGDSignalConfigs()
    {
        var configs = new List<GDSignalConfig>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, Name, ApiBaseUrl, ApiToken, Strategys,
                   MonitorStartTime, MonitorEndTime, MonitorNightSession,
                   MonitorIntervalMinutes, UseFixedTimePoints, FixedTimeMinutes,
                   TerminalId, EnableText, EnableImage, IsEnabled,
                   Conditions, EnableGD15, EnableGD20, EnableGD25, EnableGD30, EnableGD35, EnableGD40,
                   EnableRealTimeStopPriceDiffRateCondition, EnableRemainingRiskCondition,
                   RealTimeStopPriceDiffRateValue, RemainingRiskValue,
                   CreatedAt, UpdatedAt
            FROM GDSignalConfigs
            ORDER BY CreatedAt ASC
        ";

        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                configs.Add(new GDSignalConfig
                {
                    Id = reader.GetInt32(0),
                    Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    ApiBaseUrl = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    ApiToken = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Strategys = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    MonitorStartTime = reader.IsDBNull(5) ? "09:00" : reader.GetString(5),
                    MonitorEndTime = reader.IsDBNull(6) ? "15:00" : reader.GetString(6),
                    MonitorNightSession = reader.GetInt32(7) == 1,
                    MonitorIntervalMinutes = reader.IsDBNull(8) ? 30 : reader.GetInt32(8),
                    UseFixedTimePoints = reader.IsDBNull(9) ? true : reader.GetInt32(9) == 1,
                    FixedTimeMinutes = reader.IsDBNull(10) ? "0,15,30,45" : reader.GetString(10),
                    TerminalId = reader.IsDBNull(11) ? "" : reader.GetString(11),
                    EnableText = reader.IsDBNull(12) ? true : reader.GetInt32(12) == 1,
                    EnableImage = reader.IsDBNull(13) ? false : reader.GetInt32(13) == 1,
                    IsEnabled = reader.IsDBNull(14) ? false : reader.GetInt32(14) == 1,
                    Conditions = reader.IsDBNull(15) ? "[]" : reader.GetString(15),
                    EnableGD15 = reader.IsDBNull(16) ? false : reader.GetInt32(16) == 1,
                    EnableGD20 = reader.IsDBNull(17) ? false : reader.GetInt32(17) == 1,
                    EnableGD25 = reader.IsDBNull(18) ? false : reader.GetInt32(18) == 1,
                    EnableGD30 = reader.IsDBNull(19) ? false : reader.GetInt32(19) == 1,
                    EnableGD35 = reader.IsDBNull(20) ? false : reader.GetInt32(20) == 1,
                    EnableGD40 = reader.IsDBNull(21) ? false : reader.GetInt32(21) == 1,
                    EnableRealTimeStopPriceDiffRateCondition = reader.IsDBNull(22) ? true : reader.GetInt32(22) == 1,
                    EnableRemainingRiskCondition = reader.IsDBNull(23) ? false : reader.GetInt32(23) == 1,
                    RealTimeStopPriceDiffRateValue = reader.IsDBNull(24) ? 0 : reader.GetDouble(24),
                    RemainingRiskValue = reader.IsDBNull(25) ? 0 : reader.GetDouble(25),
                    CreatedAt = DateTime.Parse(reader.GetString(26)),
                    UpdatedAt = DateTime.Parse(reader.GetString(27))
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"获取GD信号配置错误: {ex.Message}");
        }

        return configs;
    }

    public void SaveGDSignalConfig(GDSignalConfig config)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var now = DateTime.Now.ToString("o");

        if (config.Id > 0)
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE GDSignalConfigs
                SET Name = $name, ApiBaseUrl = $apiBaseUrl, ApiToken = $apiToken,
                    Strategys = $strategys, MonitorStartTime = $monitorStartTime,
                    MonitorEndTime = $monitorEndTime, MonitorNightSession = $monitorNightSession,
                    NightSessionStartTime = $nightSessionStartTime, NightSessionEndTime = $nightSessionEndTime,
                    MonitorIntervalMinutes = $monitorIntervalMinutes, UseFixedTimePoints = $useFixedTimePoints,
                    FixedTimeMinutes = $fixedTimeMinutes,
                    TerminalId = $terminalId, EnableText = $enableText, EnableImage = $enableImage,
                    IsEnabled = $isEnabled, Conditions = $conditions,
                    EnableGD15 = $enableGD15, EnableGD20 = $enableGD20, EnableGD25 = $enableGD25,
                    EnableGD30 = $enableGD30, EnableGD35 = $enableGD35, EnableGD40 = $enableGD40,
                    EnableRealTimeStopPriceDiffRateCondition = $enableRealTimeStop, EnableRemainingRiskCondition = $enableRemainingRisk,
                    RealTimeStopPriceDiffRateValue = $realTimeStopValue, RemainingRiskValue = $remainingRiskValue,
                    UpdatedAt = $updatedAt
                WHERE Id = $id
            ";
            command.Parameters.AddWithValue("$id", config.Id);
            command.Parameters.AddWithValue("$name", config.Name ?? "");
            command.Parameters.AddWithValue("$apiBaseUrl", config.ApiBaseUrl ?? "");
            command.Parameters.AddWithValue("$apiToken", config.ApiToken ?? "");
            command.Parameters.AddWithValue("$strategys", config.Strategys ?? "");
            command.Parameters.AddWithValue("$monitorStartTime", config.MonitorStartTime ?? "09:00");
            command.Parameters.AddWithValue("$monitorEndTime", config.MonitorEndTime ?? "15:00");
            command.Parameters.AddWithValue("$monitorNightSession", config.MonitorNightSession ? 1 : 0);
            command.Parameters.AddWithValue("$nightSessionStartTime", config.NightSessionStartTime ?? "21:00");
            command.Parameters.AddWithValue("$nightSessionEndTime", config.NightSessionEndTime ?? "02:30");
            command.Parameters.AddWithValue("$monitorIntervalMinutes", config.MonitorIntervalMinutes);
            command.Parameters.AddWithValue("$useFixedTimePoints", config.UseFixedTimePoints ? 1 : 0);
            command.Parameters.AddWithValue("$fixedTimeMinutes", config.FixedTimeMinutes ?? "0,15,30,45");
            command.Parameters.AddWithValue("$terminalId", config.TerminalId ?? "");
            command.Parameters.AddWithValue("$enableText", config.EnableText ? 1 : 0);
            command.Parameters.AddWithValue("$enableImage", config.EnableImage ? 1 : 0);
            command.Parameters.AddWithValue("$isEnabled", config.IsEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$conditions", config.Conditions ?? "[]");
            command.Parameters.AddWithValue("$enableGD15", config.EnableGD15 ? 1 : 0);
            command.Parameters.AddWithValue("$enableGD20", config.EnableGD20 ? 1 : 0);
            command.Parameters.AddWithValue("$enableGD25", config.EnableGD25 ? 1 : 0);
            command.Parameters.AddWithValue("$enableGD30", config.EnableGD30 ? 1 : 0);
            command.Parameters.AddWithValue("$enableGD35", config.EnableGD35 ? 1 : 0);
            command.Parameters.AddWithValue("$enableGD40", config.EnableGD40 ? 1 : 0);
            command.Parameters.AddWithValue("$enableRealTimeStop", config.EnableRealTimeStopPriceDiffRateCondition ? 1 : 0);
            command.Parameters.AddWithValue("$enableRemainingRisk", config.EnableRemainingRiskCondition ? 1 : 0);
            command.Parameters.AddWithValue("$realTimeStopValue", config.RealTimeStopPriceDiffRateValue);
            command.Parameters.AddWithValue("$remainingRiskValue", config.RemainingRiskValue);
            command.Parameters.AddWithValue("$updatedAt", now);
            command.ExecuteNonQuery();
        }
        else
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO GDSignalConfigs (Name, ApiBaseUrl, ApiToken, Strategys,
                    MonitorStartTime, MonitorEndTime, MonitorNightSession,
                    NightSessionStartTime, NightSessionEndTime,
                    MonitorIntervalMinutes, UseFixedTimePoints, FixedTimeMinutes,
                    TerminalId, EnableText, EnableImage, IsEnabled,
                    Conditions, EnableGD15, EnableGD20, EnableGD25, EnableGD30, EnableGD35, EnableGD40,
                    EnableRealTimeStopPriceDiffRateCondition, EnableRemainingRiskCondition,
                    RealTimeStopPriceDiffRateValue, RemainingRiskValue,
                    CreatedAt, UpdatedAt)
                VALUES ($name, $apiBaseUrl, $apiToken, $strategys,
                    $monitorStartTime, $monitorEndTime, $monitorNightSession,
                    $nightSessionStartTime, $nightSessionEndTime,
                    $monitorIntervalMinutes, $useFixedTimePoints, $fixedTimeMinutes,
                    $terminalId, $enableText, $enableImage, $isEnabled,
                    $conditions, $enableGD15, $enableGD20, $enableGD25, $enableGD30, $enableGD35, $enableGD40,
                    $enableRealTimeStop, $enableRemainingRisk, $realTimeStopValue, $remainingRiskValue,
                    $createdAt, $updatedAt)
            ";
            command.Parameters.AddWithValue("$name", config.Name ?? "");
            command.Parameters.AddWithValue("$apiBaseUrl", config.ApiBaseUrl ?? "");
            command.Parameters.AddWithValue("$apiToken", config.ApiToken ?? "");
            command.Parameters.AddWithValue("$strategys", config.Strategys ?? "");
            command.Parameters.AddWithValue("$monitorStartTime", config.MonitorStartTime ?? "09:00");
            command.Parameters.AddWithValue("$monitorEndTime", config.MonitorEndTime ?? "15:00");
            command.Parameters.AddWithValue("$monitorNightSession", config.MonitorNightSession ? 1 : 0);
            command.Parameters.AddWithValue("$nightSessionStartTime", config.NightSessionStartTime ?? "21:00");
            command.Parameters.AddWithValue("$nightSessionEndTime", config.NightSessionEndTime ?? "02:30");
            command.Parameters.AddWithValue("$monitorIntervalMinutes", config.MonitorIntervalMinutes);
            command.Parameters.AddWithValue("$useFixedTimePoints", config.UseFixedTimePoints ? 1 : 0);
            command.Parameters.AddWithValue("$fixedTimeMinutes", config.FixedTimeMinutes ?? "0,15,30,45");
            command.Parameters.AddWithValue("$terminalId", config.TerminalId ?? "");
            command.Parameters.AddWithValue("$enableText", config.EnableText ? 1 : 0);
            command.Parameters.AddWithValue("$enableImage", config.EnableImage ? 1 : 0);
            command.Parameters.AddWithValue("$isEnabled", config.IsEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$conditions", config.Conditions ?? "[]");
            command.Parameters.AddWithValue("$enableGD15", config.EnableGD15 ? 1 : 0);
            command.Parameters.AddWithValue("$enableGD20", config.EnableGD20 ? 1 : 0);
            command.Parameters.AddWithValue("$enableGD25", config.EnableGD25 ? 1 : 0);
            command.Parameters.AddWithValue("$enableGD30", config.EnableGD30 ? 1 : 0);
            command.Parameters.AddWithValue("$enableGD35", config.EnableGD35 ? 1 : 0);
            command.Parameters.AddWithValue("$enableGD40", config.EnableGD40 ? 1 : 0);
            command.Parameters.AddWithValue("$enableRealTimeStop", config.EnableRealTimeStopPriceDiffRateCondition ? 1 : 0);
            command.Parameters.AddWithValue("$enableRemainingRisk", config.EnableRemainingRiskCondition ? 1 : 0);
            command.Parameters.AddWithValue("$realTimeStopValue", config.RealTimeStopPriceDiffRateValue);
            command.Parameters.AddWithValue("$remainingRiskValue", config.RemainingRiskValue);
            command.Parameters.AddWithValue("$createdAt", now);
            command.Parameters.AddWithValue("$updatedAt", now);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteGDSignalConfig(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM GDSignalConfigs WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
