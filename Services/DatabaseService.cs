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

            PRAGMA journal_mode=WAL;
        ";
        command.ExecuteNonQuery();

        // 执行数据库迁移
        MigrateBodyColumn();
        MigrateScheduledTasksTable(connection);
        MigratePushCombinationsTable(connection);
        MigrateMessageSourcesApiToken(connection);

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
        command.CommandText = @"
            SELECT Id, MessageType, TerminalId, Method, Status, Body, ReceivedAt, ProcessedAt, CreatedAt, SourceId
            FROM FeishuMessages
            WHERE Status = $status
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
}
