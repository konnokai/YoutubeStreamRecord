using Newtonsoft.Json;
using System;
using System.IO;

public class BotConfig
{
    public string GoogleApiKey { get; set; } = default;
    public string RedisOption { get; set; } = "127.0.0.1,syncTimeout=3000";
    private bool InDocker { get { return Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"; } }

    public void InitBotConfig()
    {
        if (InDocker)
        {
            Log.Info("在Docker容器內運行，從環境變數讀取設定");

            foreach (var item in GetType().GetProperties())
            {
                bool exitIfNoVar = false;
                object origValue = item.GetValue(this);
                if (origValue == default) exitIfNoVar = true;

                object setValue = GetEnvironmentVariable(item.Name, item.PropertyType, exitIfNoVar);
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
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
                throw;
            }
        }
    }

    private object GetEnvironmentVariable(string varName, Type T, bool exitIfNoVar = false)
    {
        string value = Environment.GetEnvironmentVariable(varName);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (exitIfNoVar)
            {
                Log.Error($"{varName}遺失，請輸入至環境變數後重新運行");
                if (!Console.IsInputRedirected)
                    Console.ReadKey();
                Environment.Exit(3);
            }
            return default;
        }
        return Convert.ChangeType(value, T);
    }
}