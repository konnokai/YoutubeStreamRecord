using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using static Youtube_Stream_Record.Program;
using ResultType = Youtube_Stream_Record.Program.ResultType;

namespace Youtube_Stream_Record
{
    public static class Record
    {
        static string videoId;
        static string fileName;
        static string tempPath;
        static string outputPath;
        static string unarchivedOutputPath;
        static bool isDisableRedis;

        const string HTML = "liveStreamabilityRenderer\":{\"videoId\":\"";

        public static async Task<ResultType> StartRecord(string id, string argOutputPath, string argTempPath, string argUnArchivedOutputPath, uint startStreamLoopTime, uint checkNextStreamTime, bool isLoop = false, bool argIsDisableRedis = false, bool isDisableLiveFromStart = false)
        {
            string channelId = "", channelTitle = "";
            id = id.Replace("@", "-");
            isDisableRedis = argIsDisableRedis;

            #region 初始化
            if (!isDisableRedis)
            {
                try
                {
                    RedisConnection.Init(Utility.BotConfig.RedisOption);
                    Utility.Redis = RedisConnection.Instance.ConnectionMultiplexer;
                }
                catch (Exception ex)
                {
                    Log.Error("Redis連線錯誤，請確認伺服器是否已開啟");
                    Log.Error(ex.Message);
                    return ResultType.Error;
                }
            }

            if (id.Length == 11)
            {
                videoId = id;

                bool isError = false;
                do
                {
                    try
                    {
                        var result = await Utility.GetSnippetDataByVideoIdAsync(videoId);
                        if (result == null)
                        {
                            Log.Error($"{videoId} 不存在直播");
                            return ResultType.Error;
                        }

                        channelId = result.ChannelId;
                        channelTitle = result.ChannelTitle;
                        isError = false;
                    }
                    catch (HttpRequestException httpEx)
                    {
                        Log.Error(httpEx, "GetSnippetDataByVideoIdAsync");
                        isError = true;
                        await Task.Delay(1000);
                    }
                } while (isError);
            }
            else
            {
                try
                {
                    channelId = await Utility.GetChannelId(id).ConfigureAwait(false);
                }
                catch (FormatException fex)
                {
                    Log.Error(fex.Message);
                    return ResultType.Error;
                }
                catch (ArgumentNullException)
                {
                    Log.Error("網址不可空白");
                    return ResultType.Error;
                }

                var result = await Utility.GetChannelDataByChannelIdAsync(channelId);
                channelTitle = result.ChannelTitle;

                if (channelTitle == "")
                {
                    Log.Error($"{channelId} 不存在頻道");
                    return ResultType.Error;
                }
            }

            Log.Info($"頻道Id: {channelId}");
            Log.Info($"頻道名稱: {channelTitle}");

            if (!argOutputPath.EndsWith(Utility.GetEnvSlash()))
                argOutputPath += Utility.GetEnvSlash();
            if (!argTempPath.EndsWith(Utility.GetEnvSlash()))
                argTempPath += Utility.GetEnvSlash();
            if (!argUnArchivedOutputPath.EndsWith(Utility.GetEnvSlash()))
                argUnArchivedOutputPath += Utility.GetEnvSlash();

            outputPath = argOutputPath.Replace("\"", "");
            tempPath = argTempPath.Replace("\"", "");
            unarchivedOutputPath = argUnArchivedOutputPath.Replace("\"", "");
            Log.Info($"輸出路徑: {outputPath}");
            Log.Info($"暫存路徑: {tempPath}");
            Log.Info($"私人存檔路徑: {unarchivedOutputPath}");
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
                do
                {
                    bool hasCommingStream = false;
                    string web;
                    Utility.IsDelLive = false;

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
                                    if (Utility.IsClose) return ResultType.None;
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
                    switch (WaitForScheduledStream())
                    {
                        case Status.IsClose:
                            return ResultType.None;
                        case Status.Deleted:
                            if (!isDisableRedis)
                            {
                                Utility.Redis.GetSubscriber().Publish("youtube.deletestream", videoId);
                            }
                            continue;
                        case Status.IsChatRoom:
                            chatRoomId = videoId;
                            continue;
                        case Status.IsChangeTime:
                            //Utility.Redis.GetSubscriber().Publish("youtube.changestreamtime", videoId);
                            continue;
                    }
                    #endregion

                    bool isCanNotRecordStream = false, isReceiveDownload = false;
                    #region 3. 開始錄製直播
                    do
                    {
                        if (Utility.IsLiveEnd(videoId, true, isDisableRedis)) break;

                        fileName = $"youtube_{channelId}_{DateTime.Now:yyyyMMdd_HHmmss}_{videoId}";
                        tempPath += $"{DateTime.Now:yyyyMMdd}{Utility.GetEnvSlash()}";
                        if (!Directory.Exists(tempPath)) Directory.CreateDirectory(tempPath);
                        outputPath += $"{DateTime.Now:yyyyMMdd}{Utility.GetEnvSlash()}";
                        if (!Directory.Exists(outputPath)) Directory.CreateDirectory(outputPath);

                        CancellationTokenSource cancellationToken = new CancellationTokenSource();
                        CancellationToken token = cancellationToken.Token;

                        // 如果關閉從開頭錄影的話，每六小時需要重新開一次錄影，才能避免掉長時間錄影導致HTTP 503錯誤
                        // 但從頭開始錄影好像只能錄兩小時 :thinking:
                        string arguments = "--live-from-start -f bestvideo+bestaudio ";
                        if (isDisableLiveFromStart)
                            arguments = "-f b ";

                        Log.Info($"存檔名稱: {fileName}");
                        var process = new Process();
#if RELEASE
                        process.StartInfo.FileName = "yt-dlp";
                        string browser = "firefox";
#else
                        process.StartInfo.FileName = "yt-dlp_min.exe";
                        string browser = "chrome";
#endif

                        if (Utility.InDocker)
                            arguments += "--cookies /app/cookies.txt";
                        else
                            arguments += $"--cookies-from-browser {browser}";

                        #region 如果過了一小時還沒開始錄影就取消本次錄影
                        var task = Task.Run(async () =>
                        {
                            int waitTime = 3600;
                            do
                            {
                                waitTime--;
                                await Task.Delay(1000);
                                if (token.IsCancellationRequested)
                                    return;
                            } while (waitTime >= 0);

                            if (!isReceiveDownload)
                            {
                                Log.Warn("已等待一小時但尚未開始直播，取消錄影");
                                process.Kill(Signum.SIGQUIT);
                                isCanNotRecordStream = true;
                            }
                        });
                        #endregion

                        // --live-from-start 太吃硬碟隨機讀寫
                        // --embed-metadata --embed-thumbnail 會導致不定時卡住，先移除
                        process.StartInfo.Arguments = $"https://www.youtube.com/watch?v={videoId} -o \"{tempPath}{fileName}.%(ext)s\" --wait-for-video {startStreamLoopTime} --mark-watched {arguments}";
                        process.StartInfo.RedirectStandardOutput = true;
                        process.OutputDataReceived += (sender, e) =>
                        {
                            try
                            {
                                if (string.IsNullOrEmpty(e.Data))
                                    return;

                                // 不顯示wait開頭的訊息避免吃爆Portainer的log
                                if (e.Data.ToLower().StartsWith("[wait]"))
                                    return;

                                Console.WriteLine(e.Data);

                                // 應該能用這個來判定開始直播
                                if (e.Data.ToLower().StartsWith("[download]"))
                                {
                                    if (isReceiveDownload)
                                        return;

                                    isReceiveDownload = true;

                                    Log.Info("開始直播!");

                                    if (!isDisableRedis)
                                    {
                                        Utility.Redis.GetSubscriber().Publish("youtube.startstream", videoId);
                                        Utility.Redis.GetDatabase().SetAdd("youtube.nowRecord", videoId);

                                        if (isDisableLiveFromStart)
                                        {
                                            #region 每六小時重新執行錄影
                                            int waitTime = (5 * 60 * 60) + (59 * 60);
                                            var task = Task.Run(async () =>
                                            {
                                                do
                                                {
                                                    waitTime--;
                                                    await Task.Delay(1000);
                                                    if (token.IsCancellationRequested)
                                                        return;
                                                } while (waitTime >= 0);

                                                Utility.Redis.GetSubscriber().Publish("youtube.record", videoId);
                                            });
                                            #endregion
                                        }
                                    }
                                }

                                if (e.Data.Contains("members-only content"))
                                {
                                    Log.Error("檢測到無法讀取的會限");
                                    process.Kill(Signum.SIGQUIT);
                                    isCanNotRecordStream = true;
                                }
                                else if (e.Data.Contains("video is private") || e.Data.Contains("Private video"))
                                {
                                    Log.Error("已私人化，取消錄影");
                                    process.Kill(Signum.SIGQUIT);
                                    isCanNotRecordStream = true;
                                }
                                else if (e.Data.Contains("video has been removed"))
                                {
                                    Log.Error("已移除，取消錄影");
                                    process.Kill(Signum.SIGQUIT);
                                    isCanNotRecordStream = true;
                                }
                            }
                            catch { }
                        };

                        Log.Info(process.StartInfo.Arguments);

                        process.Start();
                        process.BeginOutputReadLine();
                        process.WaitForExit();
                        process.CancelOutputRead();

                        Utility.IsClose = true;
                        Log.Info($"錄影結束");
                        cancellationToken.Cancel();

                        // 確定直播是否結束或是否為會限直播
                        if (Utility.IsLiveEnd(videoId, false, isDisableRedis) || isCanNotRecordStream) break;
                    } while (!Utility.IsClose);
                    #endregion

                    #region 4. 直播結束後的保存處理
                    if (!isCanNotRecordStream) // 如果該直播沒被判定成不能錄影的會限直播的話
                    {
                        if (!string.IsNullOrEmpty(fileName) && Utility.IsDelLive) // 如果被刪檔就保存到unarchivedOutputPath
                        {
                            Log.Info($"已刪檔直播，移動資料");
                            MoveVideo(unarchivedOutputPath, "youtube.unarchived");
                        }
                        else if (!string.IsNullOrEmpty(fileName) && Utility.IsMemberOnly(videoId)) // 如果是會限直播也保存到unarchivedOutputPath
                        {
                            Log.Info($"已轉會限影片，移動資料");
                            MoveVideo(unarchivedOutputPath, "youtube.memberonly");
                        }
                        else if (Path.GetDirectoryName(outputPath) != Path.GetDirectoryName(tempPath)) // 否則就保存到outputPath
                        {
                            Log.Info("將直播轉移至保存點");
                            MoveVideo(outputPath, "youtube.endstream");
                        }
                    }
                    #endregion

                    if (!isDisableRedis)
                        await Utility.Redis.GetDatabase().SetRemoveAsync("youtube.nowRecord", videoId);
                } while (isLoop && !Utility.IsClose);
            }

            return isLoop ? ResultType.Loop : ResultType.Once;
        }

        private static void MoveVideo(string outputPath, string redisChannel = "")
        {
            foreach (var item in Directory.GetFiles(tempPath, $"*{videoId}.???"))
            {
                try
                {
                    Log.Info($"移動 \"{item}\" 至 \"{outputPath}{Path.GetFileName(item)}\"");
                    File.Move(item, $"{outputPath}{Path.GetFileName(item)}");
                    if (!isDisableRedis && !string.IsNullOrEmpty(redisChannel))
                        Utility.Redis.GetSubscriber().Publish(redisChannel, videoId);
                }
                catch (Exception ex)
                {
                    if (Utility.InDocker) Log.Error(ex.ToString());
                    else File.AppendAllText($"{tempPath}{fileName}_err.txt", ex.ToString());
                }
            }
        }

        private static Status WaitForScheduledStream()
        {
            #region 取得直播排程的開始時間
            var video = Utility.YouTube.Videos.List("liveStreamingDetails");
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
                return Status.Deleted;
            }

            if (videoResult.Items[0].LiveStreamingDetails.ActualEndTime.HasValue)
            {
                Log.Warn("該直播已結束，已略過");
                return Status.IsClose;
            }
            #endregion

            #region 已開播就直接錄影
            Log.Info($"直播預計開始時間: {streamScheduledStartTime}");
            if (videoResult.Items[0].LiveStreamingDetails.ActualStartTime.HasValue)
            {
                Log.Info($"已開台，直接開始錄影，開台時間: {videoResult.Items[0].LiveStreamingDetails.ActualStartTime.Value}");
            }
            #endregion
            #region 還沒開播就等待直播排程時間抵達...
            else if (streamScheduledStartTime.AddMinutes(-1) > DateTime.Now)
            {
                Log.Info("等待排程開台的時間中...");
                int i = 900;
                do
                {
                    i--;
                    Log.Debug($"剩餘: {(int)streamScheduledStartTime.Subtract(DateTime.Now).TotalSeconds}秒");
                    if (Utility.IsClose) return Status.IsClose;
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
                        return WaitForScheduledStream();
                    }
                }
                #endregion
            }
            #endregion

            return Status.Ready;
        }
    }
}
