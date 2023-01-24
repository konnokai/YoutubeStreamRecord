using Docker.DotNet;
using Docker.DotNet.Models;
using Google.Apis.YouTube.v3.Data;
using System;
using System.Collections;
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
                var snippetData = await Utility.GetSnippetDataByVideoIdAsync(videoId);
                if (snippetData == null)
                {
                    Log.Warn($"{videoId} 無直播資料，可能已被移除");
                    return;
                }

                videoId = videoId.ToString().Replace("-", "@");
                Log.Info($"{snippetData.ChannelTitle}: {snippetData.Title}");

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
                        var parms = new CreateContainerParameters();
                        parms.Image = "jun112561/youtube-record:master";
                        parms.Name = $"record-{videoId.ToString().Replace("@", "-")}-{DateTime.Now:yyyyMMdd-HHmmss}";

                        parms.Env = new List<string>
                        {
                            $"GoogleApiKey={Utility.GetEnvironmentVariable("GoogleApiKey", typeof(string), true)}",
                            $"RedisOption={Utility.GetEnvironmentVariable("RedisOption", typeof(string), true)}"
                        };

                        List<string> binds = new List<string>
                        {
                            $"{Utility.GetEnvironmentVariable("RecordPath", typeof(string), true)}:/output",
                            $"{Utility.GetEnvironmentVariable("TempPath", typeof(string), true)}:/temp_path",
                            $"{Utility.GetEnvironmentVariable("UnArchivedPath", typeof(string), true)}:/unarchived",
                            $"{Utility.GetEnvironmentVariable("CookiesFilePath", typeof(string), true)}:/app/cookies.txt"
                        };
                        parms.HostConfig = new HostConfig() { Binds = binds };

                        parms.Labels = new Dictionary<string, string>
                        {
                            { "me.konnokai.record.video.title", snippetData.Title },
                            { "me.konnokai.record.video.id", videoId.ToString().Replace("@", "-") },
                            { "me.konnokai.record.channel.title", snippetData.ChannelTitle },
                            { "me.konnokai.record.channel.id", snippetData.ChannelId }
                        };

                        // 在Docker環境內的話則直接指定預設路徑
                        parms.Entrypoint = new List<string>
                        {
                            "dotnet",
                            "Youtube Stream Record.dll",
                            "once",
                            videoId,
                            "-o",
                            "/output",
                            "-t",
                            "/temp_path",
                            "-u",
                            "/unarchived",
                            isDisableLiveFromStart ? "--disable-live-from-start" : ""
                        };

                        // 不要讓程式自己Attach以免Log混亂
                        parms.AttachStdout = false;
                        parms.AttachStdin = false;

                        // 允許另外透過其他方法Attach進去交互
                        parms.OpenStdin = true;
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
                        Process.Start("tmux", $"new-window -d -n \"{snippetData.ChannelTitle}\" {procArgs}");
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

            sub.Subscribe("youtube.memberonly", (channel, videoId) =>
            {
                Log.Warn($"已轉會限直播: {videoId}");
            });

            
            // 要考量到如果有直播超過六小時重新錄影的話則需要排除
            // 應該要從錄影時間去判斷
            sub.Subscribe("youtube.recorddone", async (channel, videoId) =>
            {
                var parms = new ContainersPruneParameters() 
                {
                    // https://github.com/dotnet/Docker.DotNet/issues/489
                    // 媽的微軟連summary都不寫是三小==
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["label"] = new Dictionary<string, bool>
                        {
                            [$"me.konnokai.record.video.id={videoId}"] = true,
                        }
                    }
                };

                try
                {
                    var containersPruneResponse = await dockerClient.Containers.PruneContainersAsync(parms);
                    Log.Info($"已清除容器: {videoId}");
                    Log.Info($"容器Id: {string.Join(", ", containersPruneResponse.ContainersDeleted)}");
                    Log.Info($"釋放空間: {containersPruneResponse.SpaceReclaimed}");
                }
                catch (Exception ex)
                {
                    Log.Error($"清除容器失敗-{videoId}: {ex}");
                }
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
