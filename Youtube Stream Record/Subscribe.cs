using Docker.DotNet;
using Docker.DotNet.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ResultType = Youtube_Stream_Record.Program.ResultType;

namespace Youtube_Stream_Record
{
    public class Subscribe
    {
        static Timer autoDeleteArchivedTimer, autoDeleteTempRecordTimer;
        public static async Task<ResultType> SubRecord(string outputPath, string tempPath, string unarchivedOutputPath, bool autoDeleteArchived, bool isDisableLiveFromStart = false)
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

            if (!outputPath.EndsWith(Utility.GetEnvSlash())) outputPath += Utility.GetEnvSlash();
            if (!unarchivedOutputPath.EndsWith(Utility.GetEnvSlash())) unarchivedOutputPath += Utility.GetEnvSlash();
            var sub = Utility.Redis.GetSubscriber();

            DockerClient dockerClient = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && Utility.InDocker)
            {
                // https://stackoverflow.com/a/1395226
                // get the file attributes for file or directory
                FileAttributes attr = File.GetAttributes(@"/app/cookies.txt");

                if (attr.HasFlag(FileAttributes.Directory))
                {
                    Log.Error($"Cookies路徑為資料夾，請確認路徑設定是否正確，已設定的路徑為: {Utility.GetEnvironmentVariable("CookiesFilePath", typeof(string), true)}");
                    return ResultType.Error;
                }

                try
                {
                    dockerClient = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
                    await dockerClient.System.PingAsync();
                    Log.Info("成功連線到docker.sock!");
                }
                catch (Exception ex)
                {
                    Log.Error("在Docker環境但無法連接到docker.sock，請確認Volume綁定是否正確");
                    Log.Error(ex.ToString());
                    return ResultType.Error;
                }
            }

            sub.Subscribe("youtube.record", async (redisChannel, videoId) =>
            {
                Log.Info($"已接收錄影請求: {videoId}");
                var channelData = await Utility.GetChannelDataByVideoIdAsync(videoId);
                videoId = videoId.ToString().Replace("-", "@");
                Log.Info(channelData.ChannelId + " / " + channelData.ChannelTitle);

                string procArgs = $"dotnet \"Youtube Stream Record.dll\" " +
                    $"once {videoId} " +
                    $"-o \"{outputPath}\" " +
                    $"-t \"{tempPath}\" " +
                    $"-u \"{unarchivedOutputPath}\"" +
                    (isDisableLiveFromStart ? " --disable-live-from-start" : "");

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (Utility.InDocker && dockerClient != null)
                    {
                        // 在Docker環境內的話則直接指定預設路徑
                        outputPath = "/output";
                        tempPath = "/temp_path";
                        unarchivedOutputPath = "/unarchived";

                        var parms = new CreateContainerParameters();
                        parms.Image = "youtube-record:latest";
                        parms.Name = $"record-{videoId.ToString().Replace("@", "-")}-{DateTime.Now:yyyyMMdd-HHmmss}";

                        parms.Env = new List<string>();
                        parms.Env.Add($"GoogleApiKey={Utility.GetEnvironmentVariable("GoogleApiKey", typeof(string), true)}");
                        parms.Env.Add($"RedisOption={Utility.GetEnvironmentVariable("RedisOption", typeof(string), true)}");

                        List<string> binds = new List<string>();
                        binds.Add($"{Utility.GetEnvironmentVariable("RecordPath", typeof(string), true)}:/output");
                        binds.Add($"{Utility.GetEnvironmentVariable("TempPath", typeof(string), true)}:/temp_path");
                        binds.Add($"{Utility.GetEnvironmentVariable("UnArchivedPath", typeof(string), true)}:/unarchived");
                        binds.Add($"{Utility.GetEnvironmentVariable("CookiesFilePath", typeof(string), true)}:/app/cookies.txt");
                        parms.HostConfig = new HostConfig() { Binds = binds };

                        parms.Labels = new Dictionary<string, string>();
                        parms.Labels.Add("Youtube Channel Id", channelData.ChannelId);
                        parms.Labels.Add("Youtube Channel Title", channelData.ChannelTitle);
                        parms.Labels.Add("Youtube Video Id", videoId.ToString().Replace("@", "-"));

                        parms.Cmd = new List<string>();
                        parms.Cmd.Add("/bin/sh"); parms.Cmd.Add("-c"); parms.Cmd.Add(procArgs);

                        parms.AttachStdout = false;
                        parms.AttachStdin = false;

                        parms.Tty = true;

                        try
                        {
                            var containerResponse = await dockerClient.Containers.CreateContainerAsync(parms, CancellationToken.None);
                            Log.Info($"已建立容器: {containerResponse.ID}");
                            ContainerStartParameters containerStartParameters = new ContainerStartParameters();

                            if (containerResponse.Warnings.Any())
                                Log.Warn($"容器警告: {string.Join('\n', containerResponse.Warnings)}");
                            else if (await dockerClient.Containers.StartContainerAsync(containerResponse.ID, new ContainerStartParameters(), CancellationToken.None))
                                Log.Info($"容器啟動成功: {containerResponse.ID}");
                            else
                                Log.Warn($"容器已建立但無法啟動: {containerResponse.ID}");
                        }
                        catch (DockerApiException dockerEx) when (dockerEx.Message.ToLower().Contains("already in use by container"))
                        {
                            Log.Warn($"已建立 {parms.Name} 的容器，略過建立");
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"建立容器 {parms.Name} 錯誤: {ex}");
                        }
                    }
                    else if (!Utility.InDocker)
                    {
                        Process.Start("tmux", $"new-window -d -n \"{channelData.ChannelTitle}\" {procArgs}");
                    }
                    else
                    {
                        Log.Error("在Docker環境內但無法建立新的容器來錄影，請確認環境是否正常");
                    }
                }
                else Process.Start(new ProcessStartInfo()
                {
                    FileName = "dotnet",
                    Arguments = procArgs.Replace("dotnet ", ""),
                    CreateNoWindow = false,
                    UseShellExecute = true
                });
            });

            sub.Subscribe("youtube.test", (channel, nope) =>
            {
                Log.Info($"已接收測試請求");
            });

            sub.Subscribe("youtube.unarchived", (channel, videoId) =>
            {
                Log.Warn($"已刪檔直播: {videoId}");
            });

            Log.Info($"訂閱模式，保存路徑: {outputPath}");
            Log.Info($"刪檔直播保存路徑: {unarchivedOutputPath}");
            Log.Info("已訂閱Redis頻道");

            Regex regex = new Regex(@"(\d{4})(\d{2})(\d{2})");

            if (Path.GetDirectoryName(outputPath) != Path.GetDirectoryName(tempPath))
            {
                autoDeleteTempRecordTimer = new Timer((obj) =>
                {
                    try
                    {
                        var list = Directory.GetDirectories(tempPath, "202?????", SearchOption.TopDirectoryOnly);
                        foreach (var item in list)
                        {
                            var regexResult = regex.Match(item);
                            if (!regexResult.Success) continue;

                            if (DateTime.Now.Subtract(Convert.ToDateTime($"{regexResult.Groups[1]}/{regexResult.Groups[2]}/{regexResult.Groups[3]}")) > TimeSpan.FromDays(2))
                            {
                                Directory.Delete(item, true);
                                Log.Info($"已刪除: {item}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex.ToString());
                    }
                }, null, TimeSpan.FromSeconds(Math.Round(Convert.ToDateTime($"{DateTime.Now.AddDays(1):yyyy/MM/dd 00:00:00}").Subtract(DateTime.Now).TotalSeconds) + 3), TimeSpan.FromDays(1));
                Log.Warn("已開啟自動刪除2天後的暫存存檔");
            }

            if (autoDeleteArchived)
            {
                autoDeleteArchivedTimer = new Timer((obj) =>
                {
                    try
                    {
                        var list = Directory.GetDirectories(outputPath, "202?????", SearchOption.TopDirectoryOnly);
                        foreach (var item in list)
                        {
                            var regexResult = regex.Match(item);
                            if (!regexResult.Success) continue;

                            if (DateTime.Now.Subtract(Convert.ToDateTime($"{regexResult.Groups[1]}/{regexResult.Groups[2]}/{regexResult.Groups[3]}")) > TimeSpan.FromDays(14))
                            {
                                Directory.Delete(item, true);
                                Log.Info($"已刪除: {item}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex.ToString());
                    }
                }, null, TimeSpan.FromSeconds(Math.Round(Convert.ToDateTime($"{DateTime.Now.AddDays(1):yyyy/MM/dd 00:00:00}").Subtract(DateTime.Now).TotalSeconds) + 3), TimeSpan.FromDays(1));
                Log.Warn("已開啟自動刪除14天後的存檔");
            }

            if (isDisableLiveFromStart)
                Log.Info("不自動從頭開始錄影");

            do { await Task.Delay(1000); }
            while (!Utility.IsClose);

            await sub.UnsubscribeAllAsync();
            Log.Info("已取消訂閱Redis頻道");

            return ResultType.Sub;
        }
    }
}
