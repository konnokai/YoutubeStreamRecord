using Newtonsoft.Json;
using System;
using System.IO;

public class BotConfig
{
    public string GoogleApiKey { get; set; } = "";
    public string RedisOption { get; set; } = "127.0.0.1,syncTimeout=3000";

    public void InitBotConfig()
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
                Log.Error("GoogleApiKey遺失，請輸入至credentials.json後重開Bot");
                if (!Console.IsInputRedirected)
                    Console.ReadKey();
                Environment.Exit(3);
            }

            GoogleApiKey = config.GoogleApiKey;
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            throw;
        }
    }
}