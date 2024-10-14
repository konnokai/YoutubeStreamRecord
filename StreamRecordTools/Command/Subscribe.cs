using Docker.DotNet;
using Docker.DotNet.Models;
using Google.Apis.YouTube.v3.Data;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static StreamRecordTools.Program;
using ResultType = StreamRecordTools.Program.ResultType;

namespace StreamRecordTools.Command
{
    public class Subscribe
    {
        static string NetworkId { get; set; } = "";
        static string OutputPath { get; set; } = "";
        static string TempPath { get; set; } = "";
        static string UnarchivedOutputPath { get; set; } = "";
        static string MemberOnlyOutputPath { get; set; } = "";
        static bool IsDisableLiveFromStart { get; set; } = false;

        static Timer autoDeleteArchivedTimer, autoDeleteTempRecordTimer, autoCheckIsLiveUnArchivedTimer;
        static DockerClient dockerClient = null;

        public static async Task<ResultType> SubRecord(SubOptions subOptions)
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

            if (!subOptions.OutputPath.EndsWith(Utility.GetEnvSlash())) subOptions.OutputPath += Utility.GetEnvSlash();
            if (!subOptions.UnarchivedOutputPath.EndsWith(Utility.GetEnvSlash())) subOptions.UnarchivedOutputPath += Utility.GetEnvSlash();
            if (!subOptions.MemberOnlyOutputPath.EndsWith(Utility.GetEnvSlash())) subOptions.MemberOnlyOutputPath += Utility.GetEnvSlash();

            Log.Info($"訂閱模式，保存路徑: {subOptions.OutputPath}");
            Log.Info($"刪檔直播保存路徑: {subOptions.UnarchivedOutputPath}");
            Log.Info($"會限直播保存路徑: {subOptions.MemberOnlyOutputPath}");

            OutputPath = subOptions.OutputPath;
            TempPath = subOptions.TempPath;
            UnarchivedOutputPath = subOptions.UnarchivedOutputPath;
            MemberOnlyOutputPath = subOptions.MemberOnlyOutputPath;
            IsDisableLiveFromStart = subOptions.DisableLiveFromStart;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && Utility.InDocker)
            {
                // https://stackoverflow.com/a/1395226
                // get the file attributes for file or directory
                FileAttributes attr = File.GetAttributes(@"/app/cookies.txt");

                if (attr.HasFlag(FileAttributes.Directory))
                {
                    Log.Error($"Cookies 路徑為資料夾，請確認路徑設定是否正確，已設定的路徑為: {Utility.GetEnvironmentVariable("CookiesFilePath", typeof(string), true)}");
                    return ResultType.Error;
                }

                try
                {
                    var checkResult = await Utility.CheckYTCookieAsync(@"/app/cookies.txt");
                    if (checkResult != Utility.CheckResult.Ok)
                    {
                        Log.Error($"Cookie 檢測失敗，請確認是否使用正確的 YouTube Cookie，檢測結果: {checkResult}");
                    }
                    else
                    {
                        Log.Info("Cookie 檢測成功");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "CheckYTCookieAsync");
                }

                try
                {
                    dockerClient = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
                    await dockerClient.System.PingAsync();
                    Log.Info("成功連線到 docker.sock!");

                }
                catch (Exception ex)
                {
                    Log.Error(ex, "在 Docker 環境但無法連接到 docker.sock，請確認 Volume 綁定是否正確");
                    return ResultType.Error;
                }

                try
                {
                    var networks = await dockerClient.Networks.ListNetworksAsync();
                    var network = networks.FirstOrDefault((x) => x.Name.EndsWith("stream-record-tools"));
                    if (network != null)
                    {
                        NetworkId = network.ID;
                    }
                    else
                    {
                        Log.Error("找不到 \"stream-record-tools\" 對應的 Docker 網路，嘗試自動建立...");
                        // 不知道是否能建立成功，而且原則上network應該要是存在的
                        // 因為用compose架的話network一定要存在才能把container打開
                        var createdNetwork = await dockerClient.Networks.CreateNetworkAsync(new NetworksCreateParameters
                        {
                            Name = "stream-record-tools",
                            Driver = "bridge",
                            Attachable = true,
                            IPAM = new IPAM()
                            {
                                Driver = "default",
                                Config = new List<IPAMConfig>()
                                {
                                    new()
                                    {
                                        Subnet = "172.28.0.0/16",
                                        Gateway= "172.28.0.1"
                                    }
                                }
                            }
                        });

                        NetworkId = createdNetwork.ID;
                    }

                    Log.Info($"Network Id: {NetworkId}");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "取得 Network Id 失敗");
                    return ResultType.Error;
                }
            }

            var sub = Utility.Redis.GetSubscriber();

            sub.Subscribe(new("youtube.record", RedisChannel.PatternMode.Literal), async (redisChannel, videoId) =>
            {
                Log.Info($"已接收 YouTube 錄影請求: {videoId}");

                await StartRecordYouTube(videoId, false);
            });

            sub.Subscribe(new("youtube.rerecord", RedisChannel.PatternMode.Literal), async (redisChannel, videoId) =>
            {
                Log.Info($"已接收 YouTube 重新錄影請求: {videoId}");

                await StartRecordYouTube(videoId, true);
            });

            sub.Subscribe(new("youtube.test", RedisChannel.PatternMode.Literal), (channel, nope) =>
            {
                Log.Info($"已接收測試請求");
            });

            sub.Subscribe(new("youtube.test.cookie", RedisChannel.PatternMode.Literal), async (channel, nope) =>
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && Utility.InDocker)
                {
                    Log.Info($"已接收 Cookie 測試請求");

                    var result = await Utility.CheckYTCookieAsync(@"/app/cookies.txt");

                    Log.Info($"測試結果: {result}");
                }
                else
                {
                    Log.Info($"已接收 Cookie 測試請求但不在 Docker 環境內，略過");
                }
            });

            sub.Subscribe(new("youtube.unarchived", RedisChannel.PatternMode.Literal), (channel, videoId) =>
            {
                Log.Warn($"YouTube 已刪檔直播: {videoId}");
            });

            sub.Subscribe(new("youtube.memberonly", RedisChannel.PatternMode.Literal), (channel, videoId) =>
            {
                Log.Warn($"YouTube 已轉會限直播: {videoId}");
            });

            sub.Subscribe(new("twitch.record", RedisChannel.PatternMode.Literal), async (redisChannel, userLogin) =>
            {
                Log.Info($"已接收 Twitch 錄影請求: {userLogin}");

                await StartRecordTwitch(userLogin);
            });

            sub.Subscribe(new("streamTools.removeById", RedisChannel.PatternMode.Literal), async (channel, containerId) =>
            {
                if (Utility.InDocker && dockerClient != null)
                {
                    await Task.Delay(10000); // 等待10秒鐘確保容器已關閉後再清理

                    try
                    {
                        await dockerClient.Containers.RemoveContainerAsync(containerId.ToString(), new ContainerRemoveParameters());
                        Log.Info($"已清除容器: {containerId}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"移除容器失敗-{containerId}");
                    }
                }
            });

            Log.Info("已訂閱 Redis 頻道");


            UptimeKumaClient.Init(Utility.BotConfig.UptimeKumaPushUrl);

            Regex regex = new(@"(\d{4})(\d{2})(\d{2})");
            Regex fileNameRegex = new(@"youtube_(?'ChannelId'[\w\-\\_]{24})_(?'Date'\d{8})_(?'Time'\d{6})_(?'VideoId'[\w\-\\_]{11})\.(?'Ext'[\w]{2,4})");

            #region 自動刪除 2 天後的暫存存檔
            if (Path.GetDirectoryName(OutputPath) != Path.GetDirectoryName(TempPath))
            {
                autoDeleteTempRecordTimer = new Timer((obj) =>
                {
                    try
                    {
                        var list = Directory.GetDirectories(TempPath, "202?????", SearchOption.TopDirectoryOnly);
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
                        Log.Error(ex, "autoDeleteTempRecordTimer");
                    }
                }, null, TimeSpan.FromSeconds(Math.Round(Convert.ToDateTime($"{DateTime.Now.AddDays(1):yyyy/MM/dd 00:00:00}").Subtract(DateTime.Now).TotalSeconds) + 3), TimeSpan.FromDays(1));
                Log.Warn("已開啟自動刪除 2 天後的暫存存檔");
            }
            #endregion

            #region 自動刪除 14 天後的存檔
            if (subOptions.AutoDeleteArchived)
            {
                autoDeleteArchivedTimer = new Timer((obj) =>
                {
                    try
                    {
                        var list = Directory.GetDirectories(OutputPath, "202?????", SearchOption.TopDirectoryOnly);
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
                        Log.Error(ex, "autoDeleteArchivedTimer");
                    }
                }, null, TimeSpan.FromSeconds(Math.Round(Convert.ToDateTime($"{DateTime.Now.AddDays(1):yyyy/MM/dd 01:00:00}").Subtract(DateTime.Now).TotalSeconds)), TimeSpan.FromDays(1));
                Log.Warn("已開啟自動刪除 14 天後的存檔");
            }
            #endregion

            #region 自動檢測昨天的私人存檔
            // 檢測昨天的直播是否有被私人但沒被移動到私人存檔保存區
            autoCheckIsLiveUnArchivedTimer = new Timer(async (obj) =>
            {
                Log.Info("開始檢測私人存檔");

                try
                {
                    foreach (var dirName in Directory.GetDirectories(OutputPath, "202?????", SearchOption.TopDirectoryOnly))
                    {
                        if (!Directory.Exists(dirName))
                        {
                            Log.Warn($"{dirName} 不存在，略過");
                            continue;
                        }

                        List<string> videoIdList = new();

                        foreach (var item in Directory.GetFiles(dirName))
                        {
                            var regexResult = fileNameRegex.Match(item);
                            if (!regexResult.Success) continue;

                            if (!videoIdList.Contains(regexResult.Groups["VideoId"].Value))
                                videoIdList.Add(regexResult.Groups["VideoId"].Value);
                        }

                        var videoDataFromApi = await Utility.GetSnippetDataByVideoIdAsync(videoIdList);
                        if (videoDataFromApi == null)
                        {
                            Log.Error("videoIdFromApi 無資料");
                            return;
                        }

                        foreach (var item in videoIdList)
                        {
                            if (!videoDataFromApi.Any((x) => x.Id == item))
                            {
                                Utility.Redis.GetSubscriber().Publish(new("youtube.unarchived", RedisChannel.PatternMode.Literal), item);
                                foreach (var videoFile in Directory.GetFiles(dirName, $"*{item}.???"))
                                {
                                    try
                                    {
                                        Log.Info($"移動 \"{videoFile}\" 至 \"{UnarchivedOutputPath}{Path.GetFileName(videoFile)}\"");
                                        File.Move($"{videoFile}", $"{UnarchivedOutputPath}{Path.GetFileName(videoFile)}");
                                    }
                                    catch (Exception ex)
                                    {
                                        if (Utility.InDocker) Log.Error(ex.ToString());
                                        else File.AppendAllText($"{videoFile}_err.txt", ex.ToString());
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "autoCheckIsLiveUnArchivedTimer");
                }
            }, null, TimeSpan.FromSeconds(Math.Round(Convert.ToDateTime($"{DateTime.Now.AddDays(1):yyyy/MM/dd 00:00:00}").Subtract(DateTime.Now).TotalSeconds) + 3), TimeSpan.FromDays(1));
            Log.Warn("已開啟自動檢測保存目錄內的私人存檔");
            #endregion

            if (subOptions.DisableLiveFromStart)
                Log.Info("不自動從頭開始錄影");

            do { await Task.Delay(1000); }
            while (!Utility.IsClose);

            await sub.UnsubscribeAllAsync();
            Log.Info("已取消訂閱 Redis 頻道");

            return ResultType.Sub;
        }

        private static async Task StartRecordYouTube(string videoId, bool dontSendStartMessage)
        {
            bool isError; VideoSnippet snippetData = null;
            do
            {
                try
                {
                    snippetData = (await Utility.GetSnippetDataAndLiveStreamingDetailsByVideoIdAsync(videoId)).VideoSnippet;
                    isError = false;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"youtube.record: {videoId}");
                    isError = true;
                    await Task.Delay(1000);
                }
            } while (isError);

            if (snippetData == null)
            {
                Log.Warn($"{videoId} 無直播資料，可能已被移除");
                return;
            }

            videoId = videoId.ToString().Replace("-", "@");
            Log.Info($"{snippetData.ChannelTitle}: {snippetData.Title}");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (Utility.InDocker && dockerClient != null)
                {
                    await StartRecordYouTubeContainer(videoId.ToString(), snippetData, dontSendStartMessage);
                }
                else if (!Utility.InDocker)
                {
                    string procArgs = $"dotnet StreamRecordTools.dll" +
                        $" yt_once {videoId}" +
                        $" -o \"{OutputPath}\"" +
                        $" -t \"{TempPath}\"" +
                        $" -u \"{UnarchivedOutputPath}\"" +
                        $" -m \"{MemberOnlyOutputPath}\"" +
                        (IsDisableLiveFromStart ? " --disable-live-from-start" : "") +
                        (dontSendStartMessage ? " --dont-send-start-message" : "");
                    Process.Start("tmux", $"new-window -d -n \"{snippetData.ChannelTitle}\" {procArgs}");
                }
                else
                {
                    Log.Error("在 Docker 環境內但無法建立新的容器來錄影，請確認環境是否正常");
                }
            }
            else
            {
                string procArgs = $"dotnet StreamRecordTools.dll" +
                    $" yt_once {videoId}" +
                    $" -o \"{OutputPath.TrimEnd(Utility.GetEnvSlash()[0])}\"" +
                    $" -t \"{TempPath.TrimEnd(Utility.GetEnvSlash()[0])}\"" +
                    $" -u \"{UnarchivedOutputPath.TrimEnd(Utility.GetEnvSlash()[0])}\"" +
                    $" -m \"{MemberOnlyOutputPath.TrimEnd(Utility.GetEnvSlash()[0])}\"" +
                    (IsDisableLiveFromStart ? " --disable-live-from-start" : "" +
                    (dontSendStartMessage ? " --dont-send-start-message" : ""));

                Process.Start(new ProcessStartInfo()
                {
                    FileName = "dotnet",
                    Arguments = procArgs.Replace("dotnet ", ""),
                    CreateNoWindow = false,
                    UseShellExecute = true
                });
            }
        }

        private static async Task StartRecordYouTubeContainer(string videoId, VideoSnippet snippetData, bool dontSendStartMessage)
        {
            var parms = new CreateContainerParameters
            {
                Image = "jun112561/stream-record-tools:master",
                Name = $"record-{videoId.Replace("@", "-")}-{DateTime.Now:yyyyMMdd-HHmmss}",

                Env = new List<string>
                {
                    $"GoogleApiKey={Utility.GetEnvironmentVariable("GoogleApiKey", typeof(string), true)}",
                    $"RedisOption={Utility.GetEnvironmentVariable("RedisOption", typeof(string), true)}"
                },

                HostConfig = new HostConfig()
                {
                    Binds = new List<string>()
                    {
                        $"{Utility.GetEnvironmentVariable("RecordPath", typeof(string), true)}:/output",
                        $"{Utility.GetEnvironmentVariable("TempPath", typeof(string), true)}:/temp_path",
                        $"{Utility.GetEnvironmentVariable("UnarchivedPath", typeof(string), true)}:/unarchived",
                        $"{Utility.GetEnvironmentVariable("MemberOnlyPath", typeof(string), true)}:/member_only",
                        $"{Utility.GetEnvironmentVariable("CookiesFilePath", typeof(string), true)}:/app/cookies.txt"
                    }
                },

                Labels = new Dictionary<string, string>
                {
                    { "me.konnokai.record.video.title", snippetData.Title },
                    { "me.konnokai.record.video.id", videoId.Replace("@", "-") },
                    { "me.konnokai.record.channel.title", snippetData.ChannelTitle },
                    { "me.konnokai.record.channel.id", snippetData.ChannelId }
                },

                NetworkingConfig = new NetworkingConfig()
                {
                    EndpointsConfig = new Dictionary<string, EndpointSettings>()
                    {
                        { "" , new EndpointSettings() { NetworkID = NetworkId } }
                    }
                },

                Cmd = new List<string>
                {
                    "yt_once_on_docker",
                    videoId,
                    "--disable-live-from-start",
                    dontSendStartMessage ? "--dont-send-start-message" : ""
                },

                // 不要讓程式自己 Attach 以免 Log 混亂
                AttachStdout = false,
                AttachStdin = false,
                AttachStderr = false,

                // 允許另外透過其他方法 Attach 進去交互
                OpenStdin = true,
                Tty = true
            };

            try
            {
                var containerResponse = await dockerClient.Containers.CreateContainerAsync(parms, CancellationToken.None);
                Log.Info($"已建立容器: {containerResponse.ID}");

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
                Log.Error(ex, $"建立容器 {parms.Name} 錯誤");
            }
        }

        private static async Task StartRecordTwitch(string userLogin)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (Utility.InDocker && dockerClient != null)
                {
                    await StartRecordTwitchContainer(userLogin);
                }
                else if (!Utility.InDocker)
                {
                    string procArgs = $"dotnet StreamRecordTools.dll" +
                        $" twitch_once {userLogin}" +
                        $" -o \"{OutputPath}\"" +
                        $" -t \"{TempPath}\"";
                    Process.Start("tmux", $"new-window -d -n \"Twitch {userLogin}\" {procArgs}");
                }
                else
                {
                    Log.Error("在 Docker 環境內但無法建立新的容器來錄影，請確認環境是否正常");
                }
            }
            else
            {
                string procArgs = $"dotnet StreamRecordTools.dll" +
                    $" twitch_once {userLogin}" +
                    $" -o \"{OutputPath.TrimEnd(Utility.GetEnvSlash()[0])}\"" +
                    $" -t \"{TempPath.TrimEnd(Utility.GetEnvSlash()[0])}\"";

                Process.Start(new ProcessStartInfo()
                {
                    FileName = "dotnet",
                    Arguments = procArgs.Replace("dotnet ", ""),
                    CreateNoWindow = false,
                    UseShellExecute = true
                });
            }
        }

        private static async Task StartRecordTwitchContainer(string userLogin)
        {
            var parms = new CreateContainerParameters
            {
                Image = "jun112561/stream-record-tools:master",
                Name = $"record-twitch-{userLogin}-{DateTime.Now:yyyyMMdd-HHmmss}",

                Env = new List<string>
                {
                    $"GoogleApiKey={Utility.GetEnvironmentVariable("GoogleApiKey", typeof(string), true)}", // 之後要移掉，目前 Program.cs 裡面有強制綁這個環境變數
                    $"TwitchCookieAuthToken={Utility.GetEnvironmentVariable("TwitchCookieAuthToken", typeof(string), true)}",
                    $"RedisOption={Utility.GetEnvironmentVariable("RedisOption", typeof(string), true)}"
                },

                HostConfig = new HostConfig()
                {
                    Binds = new List<string>()
                    {
                        $"{Utility.GetEnvironmentVariable("RecordPath", typeof(string), true)}:/output",
                        $"{Utility.GetEnvironmentVariable("TempPath", typeof(string), true)}:/temp_path"
                    }
                },

                Labels = new Dictionary<string, string>
                {
                    { "me.konnokai.record.twitch.userLogin", userLogin }
                },

                NetworkingConfig = new NetworkingConfig()
                {
                    EndpointsConfig = new Dictionary<string, EndpointSettings>()
                    {
                        { "" , new EndpointSettings() { NetworkID = NetworkId } }
                    }
                },

                Cmd = new List<string>
                {
                    "twitch_once",
                    userLogin,
                    "-o /output",
                    "-t /temp_path"
                },

                // 不要讓程式自己 Attach 以免 Log 混亂
                AttachStdout = false,
                AttachStdin = false,
                AttachStderr = false,

                // 允許另外透過其他方法 Attach 進去交互
                OpenStdin = true,
                Tty = true
            };

            try
            {
                var containerResponse = await dockerClient.Containers.CreateContainerAsync(parms, CancellationToken.None);
                Log.Info($"已建立容器: {containerResponse.ID}");

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
                Log.Error(ex, $"建立容器 {parms.Name} 錯誤");
            }
        }
    }
}
