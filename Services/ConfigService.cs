using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using InfoTransfer.Models;

namespace InfoTransfer.Services;

public class ConfigService
{
    private readonly string _configPath;
    private readonly DatabaseService _databaseService;
    private AppConfig _config;

    public AppConfig Config => _config;

    public event EventHandler? ConfigChanged;

    public ConfigService(string configPath, DatabaseService databaseService)
    {
        _configPath = configPath;
        _databaseService = databaseService;
        _config = LoadConfig();
    }

    private AppConfig LoadConfig()
    {
        if (File.Exists(_configPath))
        {
            var json = File.ReadAllText(_configPath);
            _config = JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();

            LoadTerminalConfigsFromDatabase();

            return _config;
        }
        return new AppConfig();
    }

    private void LoadTerminalConfigsFromDatabase()
    {
        try
        {
            var dbConfigs = _databaseService.GetAllTerminalConfigs();
            if (dbConfigs.Count > 0)
            {
                _config.FeishuPushConfig.Configs = dbConfigs;
            }
        }
        catch
        {
            // 数据库可能还没初始化，忽略
        }
    }

    public void SaveConfig()
    {
        var json = JsonConvert.SerializeObject(_config, Formatting.Indented);
        File.WriteAllText(_configPath, json);
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SaveTerminalConfigs(List<TerminalConfig> configs)
    {
        _databaseService.SaveAllTerminalConfigs(configs);
        _config.FeishuPushConfig.Configs = configs;
        SaveConfig();
    }

    public void ReloadConfig()
    {
        _config = LoadConfig();
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    public TerminalConfig? GetTerminalConfig(string terminalId)
    {
        return _config.FeishuPushConfig.Configs
            .FirstOrDefault(c => c.TerminalId.Equals(terminalId, StringComparison.OrdinalIgnoreCase));
    }
}
