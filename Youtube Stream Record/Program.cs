using CommandLine;
using Docker.DotNet;
using Docker.DotNet.Models;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using HtmlAgilityPack;
using Newtonsoft.Json;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Youtube_Stream_Record
{
    static class Program
    {
        public static bool InDocker { get { return Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"; } }

        const string HTML = "liveStreamabilityRenderer\":{\"videoId\":\"";

        static YouTubeService yt;
        static bool isClose = false, isDelLive = false;
        static ConnectionMultiplexer redis;
        static BotConfig botConfig = new();
        enum Status { Ready, Deleted, IsClose, IsChatRoom, IsChangeTime };
        enum ResultType { Loop, Once, Sub, Error, None }

        static void Main(string[] args)
        {
            botConfig.InitBotConfig();

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.CancelKeyPress += (sender, e) =>
            {
                isClose = true;
                e.Cancel = true;
            };

            //https://blog.miniasp.com/post/2020/07/22/How-to-handle-graceful-shutdown-in-NET-Core
            System.Runtime.Loader.AssemblyLoadContext.Default.Unloading += (ctx) => {
                isClose = true;
            };

            yt = new YouTubeService(new BaseClientService.Initializer
            {
                ApplicationName = "Bot",
                ApiKey = botConfig.GoogleApiKey,
            });

#if DEBUG
            Console.WriteLine(string.Join(' ', args));
#endif

            var result = Parser.Default.ParseArguments<LoopOptions, OnceOptions, SubOptions>(args)
                .MapResult(
                (LoopOptions lo)  => StartRecord(lo.ChannelId, lo.OutputPath, lo.TempPath, lo.UnarchivedOutputPath, lo.StartStreamLoopTime, lo.CheckNextStreamTime, true, lo.DisableRedis,lo.DisableLiveFromStart).Result,
                (OnceOptions oo) => StartRecord(oo.ChannelId, oo.OutputPath, oo.TempPath, oo.UnarchivedOutputPath, oo.StartStreamLoopTime, oo.CheckNextStreamTime, false, oo.DisableRedis, oo.DisableLiveFromStart).Result,
                (SubOptions so) => SubRecord(so.OutputPath, so.TempPath, so.UnarchivedOutputPath, so.AutoDeleteArchived,so.DisableLiveFromStart).Result,
                Error => ResultType.None);

#if DEBUG            
            if (result == ResultType.Error || result == ResultType.None)
            {
                Console.WriteLine($"({result}) Press any key to exit...");
                Console.ReadKey();
            }
#else
            if (InDocker && result == ResultType.Error)
                Environment.Exit(3);
#endif
        }

        private static async Task<ResultType> StartRecord(string Id, string outputPath,string tempPath, string unarchivedOutputPath, uint startStreamLoopTime, uint checkNextStreamTime, bool isLoop = false, bool isDisableRedis = false, bool isDisableLiveFromStart = false)
        {
            string channelId, channelTitle, videoId = "";
            Id = Id.Replace("@", "-");

            #region 初始化
            if (!isDisableRedis)
            {
                try
                {
                    RedisConnection.Init(botConfig.RedisOption);
                    redis = RedisConnection.Instance.ConnectionMultiplexer;
                }
                catch (Exception ex)
                {
                    Log.Error("Redis連線錯誤，請確認伺服器是否已開啟");
                    Log.Error(ex.Message);
                    return ResultType.Error;
                }
            }

            if (Id.Length == 11)
            {
                videoId = Id;

                var result = await GetChannelDataByVideoIdAsync(videoId);
                channelId = result.ChannelId;
                channelTitle = result.ChannelTitle;

                if (channelId == "")
                {
                    Log.Error($"{videoId} 不存在直播");
                    return ResultType.Error;
                }
            }
            else
            {
                try
                {
                    channelId = await GetChannelId(Id).ConfigureAwait(false);
                }
                catch (FormatException fex)
                {
                    Log.Error (fex.Message);
                    return ResultType.Error;
                }
                catch (ArgumentNullException)
                {
                    Log.Error("網址不可空白");
                    return ResultType.Error;
                }

                var result = await GetChannelDataByChannelIdAsync(channelId);
                channelTitle = result.ChannelTitle;

                if (channelTitle == "")
                {
                    Log.Error($"{channelId} 不存在頻道");
                    return ResultType.Error;
                }
            }

            Log.Info($"頻道Id: {channelId}");
            Log.Info($"頻道名稱: {channelTitle}");

            if (!outputPath.EndsWith("/") && !outputPath.EndsWith("\\"))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) outputPath += "\\";
                else outputPath += "/";
            }

            if (!tempPath.EndsWith("/") && !tempPath.EndsWith("\\"))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) tempPath += "\\";
                else tempPath += "/";
            }

            outputPath = outputPath.Replace("\"", "");
            tempPath = tempPath.Replace("\"", "");
            Log.Info($"輸出路徑: {outputPath}");
            Log.Info($"暫存路徑: {tempPath}");
            Log.Info($"檢測開台的間隔: {startStreamLoopTime}秒");
            if (videoId == "")
            {
                Log.Info($"檢測下個直播的間隔: {checkNextStreamTime}秒");
                if (isLoop) Log.Info("已設定為重複錄製模式");
            }
            else Log.Info("單一直播錄影模式");

            if (isDisableLiveFromStart)
                Log.Info("不自動從頭開始錄影");

            string chatRoomId = "";
            #endregion

            using (HttpClient httpClient = new HttpClient())
            {
                string fileName = "";
                do
                {
                    bool hasCommingStream = false;
                    string web;

                    #region 1. 檢測是否有直播待機所以及直播影片Id
                    if (videoId == "")
                    {
                        do
                        {
                            try
                            {
                                web = await httpClient.GetStringAsync($"https://www.youtube.com/channel/{channelId}/live");
                            }
                            catch (Exception ex)
                            {
                                if (ex.Message.Contains("404"))
                                {
                                    Log.Error("頻道Id錯誤，請確認是否正確");
                                    return ResultType.Error;
                                }

                                Log.Error("Stage1");
                                Log.Error(ex.ToString());
                                if (ex.InnerException != null) Log.Error(ex.InnerException.ToString());
                                Thread.Sleep(5000);
                                continue;
                            }

                            videoId = web.Substring(web.IndexOf(HTML) + HTML.Length, 11);
                            if (videoId.Contains("10px;font") || videoId == chatRoomId)
                            {
                                Log.Warn("該頻道尚未設定接下來的直播");
                                int num = (int)checkNextStreamTime;

                                do
                                {
                                    Log.Debug($"剩餘: {num}秒");
                                    num--;
                                    if (isClose) return ResultType.None;
                                    Thread.Sleep(1000);
                                } while (num >= 0);
                            }
                            else
                            {
                                hasCommingStream = true;
                            }
                        } while (!hasCommingStream);
                    }
                    #endregion

                    Log.Info($"要記錄的直播影片Id: {videoId}");

                    #region 2. 等待排成時間
                    switch (WaitForScheduledStream(videoId))
                    {
                        case Status.IsClose:
                            return ResultType.None;
                        case Status.Deleted:
                            if (!isDisableRedis)
                            {
                                redis.GetSubscriber().Publish("youtube.deletestream", videoId);
                            }
                            continue;
                        case Status.IsChatRoom:
                            chatRoomId = videoId;
                            continue;
                        case Status.IsChangeTime:
                            //redis.GetSubscriber().Publish("youtube.changestreamtime", videoId);
                            continue;
                    }
                    #endregion

                    #region 3. 開始錄製直播
                    do
                    {
                        #region 4. 等待開台 (作廢)
                        //int reStartStreamLoopCount = 0;
                        //do
                        //{
                        //    try
                        //    {
                        //        web = webClient.DownloadString($"https://www.youtube.com/watch?v={videoId}");
                        //    }
                        //    catch (Exception ex)
                        //    {
                        //        Log.Error("Stage2");
                        //        Log.Error(ex.Message);
                        //        Log.Error(ex.StackTrace);

                        //        if (ex.Message.Contains("(429) Too Many Requests"))
                        //        {
                        //            redis.GetSubscriber().Publish("youtube.429error", videoId);
                        //            return true;
                        //        }

                        //        Thread.Sleep(5000);
                        //        continue;
                        //    }

                        //    if (web.Contains("qualityLabel")) break;
                        //    else
                        //    {
                        //        Log.Warn("還沒開台...");
                        //        int num = (int)startStreamLoopTime;

                        //        do
                        //        {
                        //            Log.Debug($"剩餘: {num}秒");
                        //            num--;
                        //            if (isClose) return true;
                        //            Thread.Sleep(1000);
                        //        } while (num >= 0);

                        //        reStartStreamLoopCount++;
                        //        if (reRecordCount != 0 && reStartStreamLoopCount >= 20) break;
                        //        else if (reRecordCount == 0 && reStartStreamLoopCount >= 30) return true;
                        //    }
                        //} while (true);
                        #endregion

                        if (IsLiveEnd(videoId, isDisableRedis)) break;

                        fileName = $"youtube_{channelId}_{DateTime.Now:yyyyMMdd_HHmmss}_{videoId}";
                        tempPath += $"{DateTime.Now:yyyyMMdd}{GetEnvSlash()}";
                        if (!Directory.Exists(tempPath)) Directory.CreateDirectory(tempPath);
                        outputPath += $"{DateTime.Now:yyyyMMdd}{GetEnvSlash()}";
                        if (!Directory.Exists(outputPath)) Directory.CreateDirectory(outputPath);

                        if (!isDisableRedis)
                        {
                            redis.GetSubscriber().Publish("youtube.startstream", JsonConvert.SerializeObject(new StreamRecordJson() { VideoId = videoId, RecordFileName = fileName }));
                            await redis.GetDatabase().SetAddAsync("youtube.nowRecord", videoId);
                        }

                        CancellationTokenSource cancellationToken = new CancellationTokenSource();
                        CancellationToken token = cancellationToken.Token;

                        string arguments = "--live-from-start -f bestvideo+bestaudio ";
                        if (isDisableLiveFromStart) // 如果關閉從開頭錄影的話，每六小時需要重新開一次錄影，才能避免掉長時間錄影導致HTTP 503錯誤
                        {
                            arguments = "-f b ";
                            #region 每六小時重新執行錄影
                            int waitTime = (5 * 60 * 60) + (59 * 60);
                            var task = Task.Run(() =>
                            {
                                do
                                {
                                    waitTime--;
                                    Thread.Sleep(1000);
                                    if (token.IsCancellationRequested)
                                        return;
                                } while (waitTime >= 0);

                                if (!isDisableRedis) redis.GetSubscriber().Publish("youtube.record", videoId);
                            });
                            #endregion
                        }

                        Log.Info($"存檔名稱: {fileName}");
                        var process = new Process();
#if RELEASE
                        process.StartInfo.FileName = "yt-dlp";
                        string browser = "firefox";
#else
                        process.StartInfo.FileName = "yt-dlp_min.exe";
                        string browser = "chrome";
#endif

                        if (InDocker)
                            arguments += "--cookies /app/cookies.txt";
                        else
                            arguments += $"--cookies-from-browser {browser}";

                        // --live-from-start 太吃硬碟隨機讀寫
                        // --embed-metadata --embed-thumbnail 會導致不定時卡住，先移除
                        process.StartInfo.Arguments = $"https://www.youtube.com/watch?v={videoId} -o \"{tempPath}{fileName}.%(ext)s\" --wait-for-video {startStreamLoopTime} --mark-watched {arguments}";

                        Log.Info(process.StartInfo.Arguments);
                        
                        process.Start();
                        process.WaitForExit();

                        isClose = true;
                        Log.Info($"錄影結束");
                        cancellationToken.Cancel();

                        // 確定直播是否結束
                        if (IsLiveEnd(videoId, isDisableRedis)) break;                       
                    } while (!isClose);
                    #endregion
                } while (isLoop && !isClose);

                #region 5. 如果直播被砍檔就移到其他地方保存
                if (!string.IsNullOrEmpty(fileName) && isDelLive)
                {
                    Log.Info($"已刪檔直播，移動資料");
                    foreach (var item in Directory.GetFiles(tempPath, $"*{videoId}.???"))
                    {
                        try
                        {
                            Log.Info(item);
                            File.Move(item, $"{unarchivedOutputPath}{Path.GetFileName(item)}");
                            if (!isDisableRedis) redis.GetSubscriber().Publish("youtube.unarchived", videoId);                            
                        }
                        catch (Exception ex)
                        {
                            File.AppendAllText($"{tempPath}{fileName}_err.txt", ex.ToString());
                        }
                    }
                }
                #endregion
                else if (Path.GetDirectoryName(outputPath) != Path.GetDirectoryName(tempPath))
                {
                    Log.Info("將直播轉移至保存點");
                    foreach (var item in Directory.GetFiles(tempPath, $"*{videoId}.???"))
                    {
                        try
                        {
                            Log.Info(item);
                            File.Move(item, $"{outputPath}{Path.GetFileName(item)}");
                        }
                        catch (Exception ex)
                        {
                            File.AppendAllText($"{tempPath}{fileName}_err.txt", ex.ToString());
                        }
                    }
                }
                if (!isDisableRedis) await redis.GetDatabase().SetRemoveAsync("youtube.nowRecord", videoId);
            }
            return (isLoop) ? ResultType.Loop : ResultType.Once;
        }

        private static Status WaitForScheduledStream(string videoId)
        {
            #region 取得直播排程的開始時間
            var video = yt.Videos.List("liveStreamingDetails");
            video.Id = videoId;
            var videoResult = video.Execute();
            if (videoResult.Items.Count == 0)
            {
                Log.Warn($"{videoId} 待機所已刪除，重新檢測");
                return Status.Deleted;
            }
            #endregion

            #region 檢查直播排程時間
            DateTime streamScheduledStartTime;
            try
            {
               if (videoResult.Items[0].LiveStreamingDetails.ScheduledStartTime.HasValue) 
                    streamScheduledStartTime = videoResult.Items[0].LiveStreamingDetails.ScheduledStartTime.Value;
               else
                    streamScheduledStartTime = videoResult.Items[0].LiveStreamingDetails.ActualStartTime.Value;
            }
            catch (ArgumentNullException)
            {
                Log.Warn("非直播影片或已下播，已略過");
                return Status.IsChatRoom;
            }

            //if (streamScheduledStartTime > DateTime.Now.AddDays(3))
            //{
            //    Log.Warn("該待機所排定時間超過三日，已略過");
            //    return Status.IsChatRoom;
            //}

            if (videoResult.Items[0].LiveStreamingDetails.ActualEndTime != null)
            {
                Log.Warn("該直播已結束，已略過");
                return Status.IsChatRoom;
            }
            #endregion

            //redis.GetSubscriber().Publish("youtube.newstream", videoId);

            #region 等待直播排程時間抵達...
            Log.Info($"直播開始時間: {streamScheduledStartTime}");
            if (streamScheduledStartTime.AddMinutes(-1) > DateTime.Now)
            {
                Log.Info("等待排程開台的時間中...");
                int i = 900;
                do
                {
                    i--;
                    Log.Debug($"剩餘: {(int)streamScheduledStartTime.Subtract(DateTime.Now).TotalSeconds}秒");
                    if (isClose) return Status.IsClose;
                    Thread.Sleep(1000);

                    if (i <= 0)
                    {
                        videoResult = video.Execute();
                        if (videoResult.Items.Count == 0)
                        {
                            Log.Warn($"{videoId} 待機所已刪除，重新檢測");
                            return Status.Deleted;
                        }

                        var newstreamScheduledStartTime = videoResult.Items[0].LiveStreamingDetails.ScheduledStartTime.Value;
                        if (newstreamScheduledStartTime != streamScheduledStartTime)
                        {
                            Log.Warn($"{videoId} 時間已變更");
                            return Status.IsChangeTime;
                        }

                        Log.Info($"{videoId} 時間未變更");
                        i = 900;
                    }
                } while (streamScheduledStartTime.AddMinutes(-1) > DateTime.Now);

                #region 開始錄製直播前，再次檢查是否有更改直播時間或是刪除待機所
                videoResult = video.Execute();
                if (videoResult.Items.Count == 0)
                {
                    Log.Warn($"{videoId} 待機所已刪除，重新檢測");
                    return Status.Deleted;
                }

                if (videoResult.Items[0].LiveStreamingDetails.ScheduledStartTime != null)
                {
                    streamScheduledStartTime = videoResult.Items[0].LiveStreamingDetails.ScheduledStartTime.Value;
                    if (streamScheduledStartTime.AddMinutes(-2) > DateTime.Now)
                    {
                        return WaitForScheduledStream(videoId);
                    }
                }
                #endregion
            }
            #endregion

            return Status.Ready;
        }

        private static bool IsLiveEnd(string videoId, bool isDisableRedis)
        {
            var video = yt.Videos.List("snippet");
            video.Id = videoId;
            var videoResult2 = video.Execute();

            try
            {
                if (videoResult2.Items.Count == 0)
                {
                    isDelLive = true;
                    if (!isDisableRedis)
                        redis.GetSubscriber().Publish("youtube.deletestream", videoId);
                    return true;
                }
                if (videoResult2.Items[0].Snippet.LiveBroadcastContent == "none")
                {
                    if (!isDisableRedis)
                        redis.GetSubscriber().Publish("youtube.endstream", JsonConvert.SerializeObject(new StreamRecordJson() { VideoId = videoId, RecordFileName = $"youtube_{videoResult2.Items[0].Snippet.ChannelId}_{DateTime.Now:yyyyMMdd_HHmmss}_{videoId}" }));
                    return true;
                }
            }
            catch (Exception) { }

            return false;
        }

        static Timer autoDeleteArchivedTimer;
        private static async Task<ResultType> SubRecord(string outputPath, string tempPath, string unarchivedOutputPath, bool autoDeleteArchived, bool isDisableLiveFromStart = false)
        {
            try
            {
                RedisConnection.Init(botConfig.RedisOption);
                redis = RedisConnection.Instance.ConnectionMultiplexer;
            }
            catch (Exception ex)
            {
                Log.Error("Redis連線錯誤，請確認伺服器是否已開啟");
                Log.Error(ex.ToString());
                return ResultType.Error;
            }

            if (!outputPath.EndsWith(GetEnvSlash())) outputPath += GetEnvSlash();
            if (!unarchivedOutputPath.EndsWith(GetEnvSlash())) unarchivedOutputPath += GetEnvSlash();
            var sub = redis.GetSubscriber();

            DockerClient dockerClient = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && InDocker)
            {
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
                var channelData = await GetChannelDataByVideoIdAsync(videoId);
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
                    if (InDocker) // 在Docker環境內的話則直接指定預設路徑
                    {
                        outputPath = "/output";
                        tempPath = "/temp_path";
                        unarchivedOutputPath = "/unarchived";
                    }

                    if (InDocker && dockerClient != null)
                    {
                        var parms = new CreateContainerParameters();
                        parms.Image = "youtube-record:latest";
                        parms.Name = $"youtube-record-{videoId.ToString().Replace("@", "-")}";

                        parms.Env = new List<string>();
                        parms.Env.Add($"GoogleApiKey={GetEnvironmentVariable("GoogleApiKey", typeof(string), true)}");
                        parms.Env.Add($"RedisOption={GetEnvironmentVariable("RedisOption", typeof(string), true)}");

                        List<string> binds = new List<string>();
                        binds.Add($"{GetEnvironmentVariable("RecordPath", typeof(string), true)}:/output");
                        binds.Add($"{GetEnvironmentVariable("TempPath", typeof(string), true)}:/temp_path");
                        binds.Add($"{GetEnvironmentVariable("UnArchivedPath", typeof(string), true)}:/unarchived");
                        binds.Add($"{GetEnvironmentVariable("CookiesFilePath", typeof(string), true)}:/app/cookies.txt");
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
                    else if (!InDocker)
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
            if (autoDeleteArchived)
            {
                autoDeleteArchivedTimer = new Timer((obj) => 
                {
                    try
                    {
                        Regex regex = new Regex(@"(\d{4})(\d{2})(\d{2})");
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
                        list = Directory.GetDirectories(tempPath, "202?????", SearchOption.TopDirectoryOnly);
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
            while (!isClose);

            await sub.UnsubscribeAllAsync();
            Log.Info("已取消訂閱Redis頻道");

            return ResultType.Sub;
        }

        private static string GetEnvSlash()
            => (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "\\" : "/");

        public static async Task<string> GetChannelId(string channelUrl)
        {
            if (string.IsNullOrEmpty(channelUrl))
                throw new ArgumentNullException(channelUrl);

            channelUrl = channelUrl.Trim();

            switch (channelUrl.ToLower())
            {
                case "all":
                case "holo":
                case "2434":
                case "other":
                    return channelUrl.ToLower();
            }

            string channelId = "";

            Regex regex = new Regex(@"(http[s]{0,1}://){0,1}(www\.){0,1}(?'Host'[^/]+)/(?'Type'[^/]+)/(?'ChannelName'[\w%\-]+)");
            Match match = regex.Match(channelUrl);
            if (!match.Success)
                throw new UriFormatException("錯誤，請確認是否輸入YouTube頻道網址");

            if (match.Groups["Type"].Value == "channel")
            {
                channelId = match.Groups["ChannelName"].Value;
                if (!channelId.StartsWith("UC")) throw new UriFormatException("錯誤，頻道Id格式不正確");
                if (channelId.Length != 24) throw new UriFormatException("錯誤，頻道Id字元數不正確");
            }
            else if (match.Groups["Type"].Value == "c")
            {
                string channelName = WebUtility.UrlDecode(match.Groups["ChannelName"].Value);

                if (await redis.GetDatabase().KeyExistsAsync($"discord_stream_bot:ChannelNameToId:{channelName}"))
                {
                    channelId = await redis.GetDatabase().StringGetAsync($"discord_stream_bot:ChannelNameToId:{channelName}");
                }
                else
                {
                    try
                    {
                        //https://stackoverflow.com/a/36559834
                        HtmlWeb htmlWeb = new HtmlWeb();
                        var htmlDocument = await htmlWeb.LoadFromWebAsync($"https://www.youtube.com/c/{channelName}");
                        var node = htmlDocument.DocumentNode.Descendants().FirstOrDefault((x) => x.Name == "meta" && x.Attributes.Any((x2) => x2.Name == "itemprop" && x2.Value == "channelId"));
                        if (node == null)
                            throw new UriFormatException("錯誤，請確認是否輸入正確的YouTube頻道網址\n" +
                                "或確認該頻道是否存在");

                        channelId = node.Attributes.FirstOrDefault((x) => x.Name == "content").Value;
                        if (string.IsNullOrEmpty(channelId))
                            throw new UriFormatException("錯誤，請確認是否輸入正確的YouTube頻道網址\n" +
                                "或確認該頻道是否存在");

                        await redis.GetDatabase().StringSetAsync($"discord_stream_bot:ChannelNameToId:{channelName}", channelId);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(channelUrl);
                        Log.Error(ex.ToString());
                        throw;
                    }
                }
            }

            return channelId;
        }

        private static async Task<(string ChannelId, string ChannelTitle)> GetChannelDataByChannelIdAsync(string channelId)
        {
            try
            {
                var channel = yt.Channels.List("snippet");
                channel.Id = channelId;
                var response = await channel.ExecuteAsync().ConfigureAwait(false);
                return (channelId, response.Items[0].Snippet.Title);
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message + "\r\n" + ex.StackTrace);
                return ("", "");
            }
        }

        private static async Task<(string ChannelId, string ChannelTitle)> GetChannelDataByVideoIdAsync(string videoId)
        {
            try
            {
                var video = yt.Videos.List("snippet");
                video.Id = videoId;
                var response = await video.ExecuteAsync().ConfigureAwait(false);
                return (response.Items[0].Snippet.ChannelId, response.Items[0].Snippet.ChannelTitle);
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message + "\r\n" + ex.StackTrace);
                return ("", "");
            }
        }

        public static string GetCommandLine(this Process process)
        {
            if (!OperatingSystem.IsWindows()) return "";

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + process.Id))
                using (ManagementObjectCollection objects = searcher.Get())
                {
                    return objects.Cast<ManagementBaseObject>().SingleOrDefault()?["CommandLine"]?.ToString();
                }
            }
            catch
            {
                return "";
            }
        }

        public static object GetEnvironmentVariable(string varName, Type T, bool exitIfNoVar = false)
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

        public class RequiredOptions
        {
            [Option('o', "output", Required = true, HelpText = "輸出路徑")]
            public string OutputPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;

            [Option('t', "temp-path", Required = false, HelpText = "暫存路徑")]
            public string TempPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;

            [Option('u', "unarchived-output", Required = true, HelpText = "刪檔直播輸出路徑")]
            public string UnarchivedOutputPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;

            [Option('s', "disable-live-from-start", Required = false, HelpText = "不從直播開頭錄影，如錄影環境無SSD且須大量同時錄影請開啟本選項")]
            public bool DisableLiveFromStart { get; set; } = false;
        }

        public class RecordOptions : RequiredOptions
        {
            [Value(0, Required = true, HelpText = "頻道網址或直播Id")]
            public string ChannelId { get; set; }

            [Value(1, Required = false, HelpText = "檢測開台的間隔")]
            public uint StartStreamLoopTime { get; set; } = 30;

            [Value(2, Required = false, HelpText = "檢測下個直播的間隔")]
            public uint CheckNextStreamTime { get; set; } = 600;

            [Option('d', "disable-redis", Required = false, HelpText = "不使用Redis")]
            public bool DisableRedis { get; set; } = false;
        }

        [Verb("loop", HelpText = "重複錄影")]
        public class LoopOptions : RecordOptions { }

        [Verb("once", HelpText = "單次錄影")]
        public class OnceOptions : RecordOptions { }
        
        [Verb("sub", HelpText = "訂閱式錄影，此模式需要搭配特定軟體使用，請勿使用")]
        public class SubOptions : RequiredOptions
        {
            [Option('d', "audo-delete", Required = false, HelpText = "自動刪除超過14天的存檔", Default = false)]
            public bool AutoDeleteArchived { get; set; }
        }
    }

    class StreamRecordJson
    {
        public string VideoId { get; set; }
        public string RecordFileName { get; set; }
    }
}