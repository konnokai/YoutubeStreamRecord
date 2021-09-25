using CommandLine;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Newtonsoft.Json;
using StackExchange.Redis;
using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Youtube_Stream_Record
{
    static class Program
    {
        const string HTML = "liveStreamabilityRenderer\":{\"videoId\":\"";

        static YouTubeService yt;
        static bool isClose = false;
        static ConnectionMultiplexer redis;
        static BotConfig botConfig = new();
        enum Status { Ready, Deleted, IsClose, IsChatRoom, IsChangeTime };

        static void Main(string[] args)
        {
            botConfig.InitBotConfig();

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.CancelKeyPress += (sender, e) =>
            {
                isClose = true;
                e.Cancel = true;
            };

            yt = new YouTubeService(new BaseClientService.Initializer
            {
                ApplicationName = "Bot",
                ApiKey = botConfig.GoogleApiKey,
            });

            var result = Parser.Default.ParseArguments<LoopOptions, OnceOptions, SubOptions>(args)
                .MapResult(
                (LoopOptions lo)  => StartRecord(lo.ChannelId, lo.OutputPath, lo.StartStreamLoopTime, lo.CheckNextStreamTime, true).Result,
                (OnceOptions oo) => StartRecord(oo.ChannelId, oo.OutputPath, oo.StartStreamLoopTime, oo.CheckNextStreamTime).Result,
                (SubOptions so) => SubRecord(so.OutputPath).Result,
                Error => false);
        }

        private static async Task<bool> StartRecord(string Id, string outputPath, uint startStreamLoopTime, uint checkNextStreamTime, bool isLoop = false)
        {
            string channelId, channelTitle, videoId = "";

            #region 初始化
            if (Id.Length == 11)
            {
                videoId = Id;

                var result = await GetChannelDataByVideoIdAsync(videoId);
                channelId = result.ChannelId;
                channelTitle = result.ChannelTitle;

                if (channelId == "")
                {
                    Log.Error($"{videoId} 不存在直播");
                    return true;
                }
            }
            else
            {
                channelId = Id;

                if (!channelId.Contains("UC"))
                {
                    Log.Error("頻道Id錯誤");
                    return true;
                }

                try
                {
                    channelId = channelId.Substring(channelId.IndexOf("UC"), 24);
                }
                catch
                {
                    Log.Error("頻道Id格式錯誤，需為24字數");
                    return true;
                }

                var result = await GetChannelDataByChannelIdAsync(channelId);
                channelTitle = result.ChannelTitle;

                if (channelTitle == "")
                {
                    Log.Error($"{channelId} 不存在頻道");
                    return true;
                }
            }

            try
            {
                RedisConnection.Init(botConfig.RedisOption);
                redis = RedisConnection.Instance.ConnectionMultiplexer;
            }
            catch (Exception ex)
            {
                Log.Error("Redis連線錯誤，請確認伺服器是否已開啟");
                Log.Error(ex.Message);
            }

            Log.Info($"頻道Id: {channelId}");
            Log.Info($"頻道名稱: {channelTitle}");

            if (!outputPath.EndsWith("/") && !outputPath.EndsWith("\\"))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) outputPath += "\\";
                else outputPath += "/";
            }

            outputPath = outputPath.Replace("\"", "");
            Log.Info($"輸出路徑: {outputPath}");
            if (videoId == "")
            {
                Log.Info($"檢測開台的間隔: {startStreamLoopTime}秒");
                Log.Info($"檢測下個直播的間隔: {checkNextStreamTime}秒");
                if (isLoop) Log.Info("已設定為重複錄製模式");
            }
            else Log.Info("單一直播錄影模式");

            string chatRoomId = "";
            #endregion

            using (WebClient webClient = new WebClient())
            {
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
                                web = webClient.DownloadString($"https://www.youtube.com/channel/{channelId}/live");
                            }
                            catch (Exception ex)
                            {
                                if (ex.Message.Contains("404"))
                                {
                                    Log.Error("頻道Id錯誤，請確認是否正確");
                                    return true;
                                }

                                Log.Error("Stage1");
                                Log.Error(ex.Message);
                                Log.Error(ex.StackTrace);
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
                                    if (isClose) return true;
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
                            return true;
                        case Status.Deleted:
                            redis.GetSubscriber().Publish("youtube.deletestream", videoId);
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
                    int reRecordCount = 0;
                    do
                    {
                        #region 4. 等待開台
                        int reStartStreamLoopCount = 0;
                        do
                        {
                            try
                            {
                                web = webClient.DownloadString($"https://www.youtube.com/watch?v={videoId}");
                            }
                            catch (Exception ex)
                            {
                                Log.Error("Stage2");
                                Log.Error(ex.Message);
                                Log.Error(ex.StackTrace);
                                Thread.Sleep(5000);
                                continue;
                            }

                            if (web.Contains("qualityLabel")) break;
                            else
                            {
                                Log.Warn("還沒開台...");
                                int num = (int)startStreamLoopTime;

                                do
                                {
                                    Log.Debug($"剩餘: {num}秒");
                                    num--;
                                    if (isClose) return true;
                                    Thread.Sleep(1000);
                                } while (num >= 0);

                                reStartStreamLoopCount++;
                                if (reRecordCount != 0 && reStartStreamLoopCount >= 20) break;
                                else if (reRecordCount == 0 && reStartStreamLoopCount >= 30) return true;
                            }
                        } while (true);
                        #endregion

                        if (IsLiveEnd(videoId)) break;

                        string fileName = $"youtube_{channelId}_{DateTime.Now:yyyyMMdd_HHmmss}_{videoId}.ts";

                        if (reRecordCount == 0 || reRecordCount == 5)
                        {
                            redis.GetSubscriber().Publish("youtube.startstream", JsonConvert.SerializeObject(new StreamRecordJson() { VideoId = videoId, RecordFileName = fileName, IsReRecord = reRecordCount == 5 }));
                            if (reRecordCount == 5) isClose = true;
                        }

                        Log.Info($"存檔名稱: {fileName}");
                        Process.Start("streamlink", $"-o \"{outputPath}{fileName}\" https://www.youtube.com/watch?v={videoId} best").WaitForExit();
                        Log.Info($"錄影結束");

                        #region 確定直播是否結束
                        if (IsLiveEnd(videoId)) break;

                        Log.Warn($"直播尚未結束，重新錄影");
                        reRecordCount++;
                        #endregion
                    } while (!isClose);
                    #endregion
                } while (isLoop && !isClose);
            }
            return true;
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
               if (videoResult.Items[0].LiveStreamingDetails.ScheduledStartTime != null) 
                    streamScheduledStartTime = videoResult.Items[0].LiveStreamingDetails.ScheduledStartTime.ConvertToDateTime();
               else
                    streamScheduledStartTime = videoResult.Items[0].LiveStreamingDetails.ActualStartTime.ConvertToDateTime();
            }
            catch (ArgumentNullException)
            {
                Log.Warn("非直播影片或已下播，已略過");
                return Status.IsChatRoom;
            }

            if (streamScheduledStartTime > DateTime.Now.AddDays(3))
            {
                Log.Warn("該待機所排定時間超過三日，已略過");
                return Status.IsChatRoom;
            }

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

                        var newstreamScheduledStartTime = videoResult.Items[0].LiveStreamingDetails.ScheduledStartTime.ConvertToDateTime();
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
                    streamScheduledStartTime = videoResult.Items[0].LiveStreamingDetails.ScheduledStartTime.ConvertToDateTime();
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

        private static bool IsLiveEnd(string videoId)
        {
            var video = yt.Videos.List("snippet");
            video.Id = videoId;
            var videoResult2 = video.Execute();

            try
            {
                if (videoResult2.Items.Count == 0)
                {
                    redis.GetSubscriber().Publish("youtube.deletestream", videoId);
                    return true;
                }
                if (videoResult2.Items[0].Snippet.LiveBroadcastContent == "none")
                {
                    redis.GetSubscriber().Publish("youtube.endstream", JsonConvert.SerializeObject(new StreamRecordJson() { VideoId = videoId, RecordFileName = $"youtube_{videoResult2.Items[0].Snippet.ChannelId}_{DateTime.Now:yyyyMMdd_HHmmss}_{videoId}.ts", IsReRecord = false }));
                    return true;
                }
            }
            catch (Exception) { }

            return false;
        }

        private static async Task<bool> SubRecord(string outputPath)
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
                return true;
            }

            var sub = redis.GetSubscriber();
            sub.Subscribe("youtube.record", async (redisChannel, videoId) =>
            {                
                Log.Info($"已接收錄影請求: {videoId}");
                var channelData = await GetChannelDataByVideoIdAsync(videoId);
                Log.Info(channelData.ChannelId + " / " + channelData.ChannelTitle);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) Process.Start("tmux", $"new-window -d -n \"{channelData.ChannelTitle}\" dotnet \"Youtube Stream Record.dll\" once {videoId} -o \"{outputPath}\"");
                else Process.Start(new ProcessStartInfo()
                {
                    FileName = "dotnet",
                    Arguments = $"\"Youtube Stream Record.dll\" once {videoId} -o \"{outputPath}\"",
                    CreateNoWindow = false,
                    UseShellExecute = true
                });
            });

            sub.Subscribe("youtube.test", (channel, nope) =>
            {
                Log.Info($"已接收測試請求");
            });

            Log.Info("已訂閱Redis頻道");

            do { await Task.Delay(1000); }
            while (!isClose);

            await sub.UnsubscribeAllAsync();
            Log.Info("已取消訂閱Redis頻道");

            return true;
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

        public static DateTime ConvertToDateTime(this string str) =>
              DateTime.Parse(str);

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

        [Verb("loop", HelpText = "重複錄影")]
        public class LoopOptions
        {
            [Value(0, Required = true, HelpText = "頻道Id")]
            public string ChannelId { get; set; }

            [Value(1, Required = false, HelpText = "檢測開台的間隔")]
            public uint StartStreamLoopTime { get; set; } = 30;

            [Value(2, Required = false, HelpText = "檢測下個直播的間隔")]
            public uint CheckNextStreamTime { get; set; } = 600;

            [Option('o', "output", Required = false, HelpText = "輸出路徑")]
            public string OutputPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;
        }

        [Verb("once", HelpText = "單次錄影")]
        public class OnceOptions
        {
            [Value(0, Required = true, HelpText = "頻道Id")]
            public string ChannelId { get; set; }

            [Value(1, Required = false, HelpText = "檢測開台的間隔")]
            public uint StartStreamLoopTime { get; set; } = 30;

            [Value(2, Required = false, HelpText = "檢測下個直播的間隔")]
            public uint CheckNextStreamTime { get; set; } = 600;

            [Option('o', "output", Required = false, HelpText = "輸出路徑")]
            public string OutputPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;
        }
        
        [Verb("sub", HelpText = "訂閱式錄影，此模式需要搭配特定軟體使用，請勿使用")]
        public class SubOptions
        {
            [Option('o', "output", Required = true, HelpText = "輸出路徑")]
            public string OutputPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;
        }
    }

    class StreamRecordJson
    {
        public string VideoId { get; set; }
        public string RecordFileName { get; set; }
        public bool IsReRecord { get; set; }
    }
}