using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using HtmlAgilityPack;
using StackExchange.Redis;
using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Youtube_Stream_Record
{
    public static class Utility
    {
        public static YouTubeService YouTube { get; set; }
        public static ConnectionMultiplexer Redis { get; set; }
        public static BotConfig BotConfig { get; set; } = new();

        public static bool IsClose { get; set; } = false;
        public static bool IsDelLive { get; set; } = false;
        public static bool InDocker { get { return Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"; } }

        public static string GetEnvSlash()
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

                if (await Redis.GetDatabase().KeyExistsAsync($"discord_stream_bot:ChannelNameToId:{channelName}"))
                {
                    channelId = await Redis.GetDatabase().StringGetAsync($"discord_stream_bot:ChannelNameToId:{channelName}");
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

                        await Redis.GetDatabase().StringSetAsync($"discord_stream_bot:ChannelNameToId:{channelName}", channelId);
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

        public static async Task<(string ChannelId, string ChannelTitle)> GetChannelDataByChannelIdAsync(string channelId)
        {
            try
            {
                var channel = YouTube.Channels.List("snippet");
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

        public static async Task<VideoSnippet> GetSnippetDataByVideoIdAsync(string videoId)
        {
            try
            {
                var video = YouTube.Videos.List("snippet");
                video.Id = videoId;
                var response = await video.ExecuteAsync().ConfigureAwait(false);
                if (!response.Items.Any())
                    return null;

                return response.Items[0].Snippet;
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message + "\r\n" + ex.StackTrace);
                return null;
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

        public static bool IsLiveEnd(string videoId, bool isFirstCheck, bool isDisableRedis)
        {
            var video = YouTube.Videos.List("snippet,liveStreamingDetails");
            video.Id = videoId;
            var videoResult2 = video.Execute();

            try
            {
                if (!videoResult2.Items.Any())
                {
                    IsDelLive = true;
                    if (isFirstCheck && !isDisableRedis)
                        Redis.GetSubscriber().Publish("youtube.deletestream", videoId);
                    return true;
                }
                if (videoResult2.Items[0].LiveStreamingDetails.ActualEndTime.HasValue)
                {                    
                    return true;
                }
            }
            catch (Exception) { }

            return false;
        }

        public static bool IsMemberOnly(string videoId)
        {
            var ct = YouTube.CommentThreads.List("snippet");
            ct.VideoId = videoId;

            try
            {
                var commentResult = ct.Execute();
            }
            catch (Exception ex) when (ex.Message.Contains("403") || ex.Message.ToLower().Contains("the request might not be properly authorized"))
            {
                Log.Warn($"此為會限影片: {videoId}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"無法檢測是否為會限影片: {videoId}");
                Log.Warn(ex.Message);
                return false;
            }

            return false;
        }
    }

    public static class ProcessUtils
    {
        [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
        private static extern int sys_kill(int pid, int sig);

        public static void Kill(this Process process, Signum sig)
        {
            sys_kill(process.Id, (int)sig);
        }
    }

    public enum Signum : int
    {
        SIGHUP = 1, // Hangup (POSIX).
        SIGINT = 2, // Interrupt (ANSI).
        SIGQUIT = 3, // Quit (POSIX).
        SIGILL = 4, // Illegal instruction (ANSI).
        SIGTRAP = 5, // Trace trap (POSIX).
        SIGABRT = 6, // Abort (ANSI).
        SIGIOT = 6, // IOT trap (4.2 BSD).
        SIGBUS = 7, // BUS error (4.2 BSD).
        SIGFPE = 8, // Floating-point exception (ANSI).
        SIGKILL = 9, // Kill, unblockable (POSIX).
        SIGUSR1 = 10, // User-defined signal 1 (POSIX).
        SIGSEGV = 11, // Segmentation violation (ANSI).
        SIGUSR2 = 12, // User-defined signal 2 (POSIX).
        SIGPIPE = 13, // Broken pipe (POSIX).
        SIGALRM = 14, // Alarm clock (POSIX).
        SIGTERM = 15, // Termination (ANSI).
        SIGSTKFLT = 16, // Stack fault.
        SIGCLD = SIGCHLD, // Same as SIGCHLD (System V).
        SIGCHLD = 17, // Child status has changed (POSIX).
        SIGCONT = 18, // Continue (POSIX).
        SIGSTOP = 19, // Stop, unblockable (POSIX).
        SIGTSTP = 20, // Keyboard stop (POSIX).
        SIGTTIN = 21, // Background read from tty (POSIX).
        SIGTTOU = 22, // Background write to tty (POSIX).
        SIGURG = 23, // Urgent condition on socket (4.2 BSD).
        SIGXCPU = 24, // CPU limit exceeded (4.2 BSD).
        SIGXFSZ = 25, // File size limit exceeded (4.2 BSD).
        SIGVTALRM = 26, // Virtual alarm clock (4.2 BSD).
        SIGPROF = 27, // Profiling alarm clock (4.2 BSD).
        SIGWINCH = 28, // Window size change (4.3 BSD, Sun).
        SIGPOLL = SIGIO, // Pollable event occurred (System V).
        SIGIO = 29, // I/O now possible (4.2 BSD).
        SIGPWR = 30, // Power failure restart (System V).
        SIGSYS = 31, // Bad system call.
        SIGUNUSED = 31
    }
}