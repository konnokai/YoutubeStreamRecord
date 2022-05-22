using CommandLine;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using HtmlAgilityPack;
using Newtonsoft.Json;
using StackExchange.Redis;
using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace Youtube_Stream_Record
{
    static class Program
    {
        const string HTML = "liveStreamabilityRenderer\":{\"videoId\":\"";

        static YouTubeService yt;
        static bool isClose = false, isDelLive = false;
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
                (LoopOptions lo)  => StartRecord(lo.ChannelId, lo.OutputPath, lo.UnarchivedOutputPath, lo.StartStreamLoopTime, lo.CheckNextStreamTime, true).Result,
                (OnceOptions oo) => StartRecord(oo.ChannelId, oo.OutputPath, oo.UnarchivedOutputPath, oo.StartStreamLoopTime, oo.CheckNextStreamTime).Result,
                (SubOptions so) => SubRecord(so.OutputPath, so.UnarchivedOutputPath).Result,
                Error => false);
        }

        private static async Task<bool> StartRecord(string Id, string outputPath, string unarchivedOutputPath, uint startStreamLoopTime, uint checkNextStreamTime, bool isLoop = false)
        {
            string channelId, channelTitle, videoId = "";

            #region 初始化
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
                try
                {
                    channelId = await GetChannelId(Id).ConfigureAwait(false);
                }
                catch (FormatException fex)
                {
                    Log.Error (fex.Message);
                    return true;
                }
                catch (ArgumentNullException)
                {
                    Log.Error("網址不可空白");
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

            Log.Info($"頻道Id: {channelId}");
            Log.Info($"頻道名稱: {channelTitle}");

            if (!outputPath.EndsWith("/") && !outputPath.EndsWith("\\"))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) outputPath += "\\";
                else outputPath += "/";
            }

            outputPath = outputPath.Replace("\"", "");
            Log.Info($"輸出路徑: {outputPath}");
            Log.Info($"檢測開台的間隔: {startStreamLoopTime}秒");
            if (videoId == "")
            {
                Log.Info($"檢測下個直播的間隔: {checkNextStreamTime}秒");
                if (isLoop) Log.Info("已設定為重複錄製模式");
            }
            else Log.Info("單一直播錄影模式");

            string chatRoomId = "";
            #endregion

            using (WebClient webClient = new WebClient())
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

                        if (IsLiveEnd(videoId)) break;

                        fileName = $"youtube_{channelId}_{DateTime.Now:yyyyMMdd_HHmmss}_{videoId}";
                        outputPath += $"{DateTime.Now:yyyyMMdd}{GetEnvSlash()}";
                        if (!Directory.Exists(outputPath)) Directory.CreateDirectory(outputPath);

                        redis.GetSubscriber().Publish("youtube.startstream", JsonConvert.SerializeObject(new StreamRecordJson() { VideoId = videoId, RecordFileName = fileName }));

                        Log.Info($"存檔名稱: {fileName}");
                        Process.Start("yt-dlp", $"https://www.youtube.com/watch?v={videoId} -o \"{outputPath}{fileName}.%(ext)s\" --wait-for-video {startStreamLoopTime} --cookies-from-browser firefox --embed-thumbnail --embed-metadata --mark-watched --hls-use-mpegts").WaitForExit();
                        isClose = true;
                        Log.Info($"錄影結束");

                        #region 確定直播是否結束
                        if (IsLiveEnd(videoId)) break;

                        //Log.Warn($"直播尚未結束，重新錄影");
                        #endregion
                    } while (!isClose);
                    #endregion
                } while (isLoop && !isClose);

                #region 5. 如果直播被砍檔就移到其他地方保存
                if (!string.IsNullOrEmpty(fileName) && isDelLive)
                {
                    foreach (var item in Directory.GetFiles(outputPath, $"{fileName}.*"))
                    {
                        try
                        {
                            File.Move(item, $"{unarchivedOutputPath}{Path.GetFileName(item)}");
                        }
                        catch (Exception ex) 
                        {
                            File.AppendAllText($"{outputPath}{fileName}_err.txt", ex.ToString());
                        }
                    }
                }
                #endregion
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
                    isDelLive = true;
                    redis.GetSubscriber().Publish("youtube.deletestream", videoId);
                    return true;
                }
                if (videoResult2.Items[0].Snippet.LiveBroadcastContent == "none")
                {
                    redis.GetSubscriber().Publish("youtube.endstream", JsonConvert.SerializeObject(new StreamRecordJson() { VideoId = videoId, RecordFileName = $"youtube_{videoResult2.Items[0].Snippet.ChannelId}_{DateTime.Now:yyyyMMdd_HHmmss}_{videoId}" }));
                    return true;
                }
            }
            catch (Exception) { }

            return false;
        }

        private static async Task<bool> SubRecord(string outputPath, string unarchivedOutputPath)
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

            if (!outputPath.EndsWith(GetEnvSlash())) outputPath += GetEnvSlash();
            if (!unarchivedOutputPath.EndsWith(GetEnvSlash())) unarchivedOutputPath += GetEnvSlash();
            var sub = redis.GetSubscriber();

            sub.Subscribe("youtube.record", async (redisChannel, videoId) =>
            {
                Log.Info($"已接收錄影請求: {videoId}");
                var channelData = await GetChannelDataByVideoIdAsync(videoId);
                Log.Info(channelData.ChannelId + " / " + channelData.ChannelTitle);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) Process.Start("tmux", $"new-window -d -n \"{channelData.ChannelTitle}\" dotnet \"Youtube Stream Record.dll\" once {videoId} -o \"{outputPath}\" -u \"{unarchivedOutputPath}\"");
                else Process.Start(new ProcessStartInfo()
                {
                    FileName = "dotnet",
                    Arguments = $"\"Youtube Stream Record.dll\" once {videoId} -o \"{outputPath}\" -u \"{unarchivedOutputPath}\"",
                    CreateNoWindow = false,
                    UseShellExecute = true
                });
            });

            sub.Subscribe("youtube.test", (channel, nope) =>
            {
                Log.Info($"已接收測試請求");
            });

            Log.Info($"訂閱模式，保存路徑: {outputPath}");
            Log.Info($"刪檔直播保存路徑: {unarchivedOutputPath}");
            Log.Info("已訂閱Redis頻道");

            do { await Task.Delay(1000); }
            while (!isClose);

            await sub.UnsubscribeAllAsync();
            Log.Info("已取消訂閱Redis頻道");

            return true;
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
            [Value(0, Required = true, HelpText = "頻道網址或直播Id")]
            public string ChannelId { get; set; }

            [Value(1, Required = false, HelpText = "檢測開台的間隔")]
            public uint StartStreamLoopTime { get; set; } = 30;

            [Value(2, Required = false, HelpText = "檢測下個直播的間隔")]
            public uint CheckNextStreamTime { get; set; } = 600;

            [Option('o', "output", Required = false, HelpText = "輸出路徑")]
            public string OutputPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;

            [Option('u', "unarchivedoutput", Required = true, HelpText = "刪檔直播輸出路徑")]
            public string UnarchivedOutputPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;
        }

        [Verb("once", HelpText = "單次錄影")]
        public class OnceOptions
        {
            [Value(0, Required = true, HelpText = "頻道網址或直播Id")]
            public string ChannelId { get; set; }

            [Value(1, Required = false, HelpText = "檢測開台的間隔")]
            public uint StartStreamLoopTime { get; set; } = 30;

            [Value(2, Required = false, HelpText = "檢測下個直播的間隔")]
            public uint CheckNextStreamTime { get; set; } = 600;

            [Option('o', "output", Required = false, HelpText = "輸出路徑")]
            public string OutputPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;

            [Option('u', "unarchivedoutput", Required = true, HelpText = "刪檔直播輸出路徑")]
            public string UnarchivedOutputPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;
        }
        
        [Verb("sub", HelpText = "訂閱式錄影，此模式需要搭配特定軟體使用，請勿使用")]
        public class SubOptions
        {
            [Option('o', "output", Required = true, HelpText = "輸出路徑")]
            public string OutputPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;

            [Option('u', "unarchivedoutput", Required = true, HelpText = "刪檔直播輸出路徑")]
            public string UnarchivedOutputPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;
        }
    }

    class StreamRecordJson
    {
        public string VideoId { get; set; }
        public string RecordFileName { get; set; }
    }
}