using StackExchange.Redis;
using System;
using System.Diagnostics;
using System.IO;
using static StreamRecordTools.Program;
using ResultType = StreamRecordTools.Program.ResultType;

namespace StreamRecordTools.Command.Record
{
    public class Twitch
    {
        static string userLogin;
        static string fileName;
        static string tempPath;
        static string outputPath;
        static bool isDisableRedis;

        static readonly string _twitchOAuthToken = Utility.GetEnvironmentVariable("TwitchCookieAuthToken", typeof(string), true).ToString();

        public static ResultType StartRecord(TwitchOnceOptions options)
        {
            try
            {
                RedisConnection.Init(Utility.BotConfig.RedisOption);
                Utility.Redis = RedisConnection.Instance.ConnectionMultiplexer;
            }
            catch (Exception ex)
            {
                Log.Error("Redis連線錯誤，請確認伺服器是否已開啟");
                Log.Error(ex.ToString());
                return ResultType.Error;
            }

            isDisableRedis = options.DisableRedis;

            if (!options.OutputPath.EndsWith(Utility.GetEnvSlash()))
                options.OutputPath += Utility.GetEnvSlash();
            if (!options.TempPath.EndsWith(Utility.GetEnvSlash()))
                options.TempPath += Utility.GetEnvSlash();

            outputPath = options.OutputPath.Replace("\"", "").Trim();
            tempPath = options.TempPath.Replace("\"", "").Trim();

            tempPath += $"{DateTime.Now:yyyyMMdd}{Utility.GetEnvSlash()}";
            if (!Directory.Exists(tempPath)) Directory.CreateDirectory(tempPath);
            outputPath += $"{DateTime.Now:yyyyMMdd}{Utility.GetEnvSlash()}";
            if (!Directory.Exists(outputPath)) Directory.CreateDirectory(outputPath);


            Log.Info($"輸出路徑: {outputPath}");
            Log.Info($"暫存路徑: {tempPath}");

            userLogin = options.UserLogin;
            fileName = $"[{userLogin}] - {DateTime.Now:yyyyMMdd_HHmmss}.ts";

            string procArgs = $"--twitch-disable-ads --output \"{tempPath}{fileName}\"";
            if (!string.IsNullOrEmpty(_twitchOAuthToken) && _twitchOAuthToken.Length == 30)
                procArgs += $" \"--twitch-api-header=Authorization=OAuth {_twitchOAuthToken}\"";

            procArgs += $"https://twitch.tv/{userLogin} best";

            var process = new Process();
            process.StartInfo.FileName = "streamlink";
            process.StartInfo.Arguments = procArgs;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardOutput = true;

            process.ErrorDataReceived += (sender, e) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(e.Data))
                        return;

                    Log.Error(e.Data);
                }
                catch { }
            };

            process.OutputDataReceived += (sender, e) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(e.Data))
                        return;

                    Log.YouTubeInfo(e.Data);
                }
                catch { }
            };

            Log.Info(process.StartInfo.Arguments);

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            process.WaitForExit();
            process.CancelErrorRead();
            process.CancelOutputRead();

            if (Path.GetDirectoryName(outputPath) != Path.GetDirectoryName(tempPath))
            {
                Log.Info("將直播轉移至保存點");
                MoveVideo(outputPath);

                // https://social.msdn.microsoft.com/Forums/en-US/c2c12a9f-dc4c-4c9a-b652-65374ef999d8/get-docker-container-id-in-code?forum=aspdotnetcore
                if (Utility.InDocker && !isDisableRedis)
                    Utility.Redis.GetSubscriber().Publish(new("streamTools.removeById", RedisChannel.PatternMode.Literal), Environment.MachineName);
            }

            return ResultType.Once;
        }

        private static void MoveVideo(string outputPath)
        {
            foreach (var item in Directory.GetFiles(tempPath, $"*{userLogin}.???"))
            {
                try
                {
                    Log.Info($"移動 \"{item}\" 至 \"{outputPath}{Path.GetFileName(item)}\"");
                    File.Move(item, $"{outputPath}{Path.GetFileName(item)}");
                }
                catch (Exception ex)
                {
                    if (Utility.InDocker) Log.Error(ex.ToString());
                    else File.AppendAllText($"{tempPath}{fileName}_err.txt", ex.ToString());
                }
            }
        }
    }
}
