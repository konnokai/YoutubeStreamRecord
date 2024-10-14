using Newtonsoft.Json;
using StreamRecordTools;
using System;
using System.IO;

public class BotConfig
{
    public string GoogleApiKey { get; set; } = default;
    public string RedisOption { get; set; } = "127.0.0.1,syncTimeout=3000";
    public string UptimeKumaPushUrl { get; set; } = "";

    public void InitBotConfig()
    {
        if (Utility.InDocker)
        {
            Log.Info("從環境變數讀取設定");

            foreach (var item in GetType().GetProperties())
            {
                bool exitIfNoVar = false;
                object origValue = item.GetValue(this);
                if (origValue == default) exitIfNoVar = true;

                object setValue = Utility.GetEnvironmentVariable(item.Name, item.PropertyType, exitIfNoVar);
                if (setValue == null) setValue = origValue;

                item.SetValue(this, setValue);
            }
        }
        else
        {
            try { File.WriteAllText("bot_config_example.json", JsonConvert.SerializeObject(new BotConfig(), Formatting.Indented)); } catch { }
            if (!File.Exists("bot_config.json"))
            {
                Log.Error($"bot_config.json遺失，請依照 {Path.GetFullPath("bot_config_example.json")} 內的格式填入正確的數值");
                if (!Console.IsInputRedirected)
                    Console.ReadKey();
                Environment.Exit(3);
            }

            var config = JsonConvert.DeserializeObject<BotConfig>(File.ReadAllText("bot_config.json"));

            try
            {
                if (string.IsNullOrWhiteSpace(config.GoogleApiKey))
                {
                    Log.Error("GoogleApiKey遺失，請輸入至bot_config.json後重開程式");
                    if (!Console.IsInputRedirected)
                        Console.ReadKey();
                    Environment.Exit(3);
                }

                GoogleApiKey = config.GoogleApiKey;
                RedisOption = config.RedisOption;
                UptimeKumaPushUrl = config.UptimeKumaPushUrl;
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
                throw;
            }
        }
    }
}