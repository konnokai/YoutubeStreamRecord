using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Polly;
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
using System.Threading.Tasks;

namespace StreamRecordTools
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

        public static async Task<(VideoSnippet VideoSnippet, VideoLiveStreamingDetails VideoLiveStreamingDetails)> GetSnippetDataAndLiveStreamingDetailsByVideoIdAsync(string videoId)
        {
            try
            {
                var video = YouTube.Videos.List("snippet,liveStreamingDetails");
                video.Id = videoId;

                var response = await video.ExecuteAsync();
                if (!response.Items.Any())
                    return (null, null);

                return (response.Items[0].Snippet, response.Items[0].LiveStreamingDetails);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GetSnippetDataByVideoIdAsync-Singel");
                throw;
            }
        }

        public static async Task<IList<Video>> GetSnippetDataByVideoIdAsync(IEnumerable<string> videoId)
        {
            var pBreaker = Policy<VideoListResponse>
               .Handle<Exception>()
               .WaitAndRetryAsync(new TimeSpan[]
               {
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(4)
               });

            List<Video> videos = new List<Video>();
            for (int i = 0; i < videoId.Count(); i += 50)
            {
                try
                {
                    var video = YouTube.Videos.List("snippet");
                    video.Id = string.Join(',', videoId.Skip(i).Take(50));
                    var response = await pBreaker.ExecuteAsync(() => video.ExecuteAsync());
                    if (!response.Items.Any())
                        continue;

                    videos.AddRange(response.Items);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "GetSnippetDataByVideoIdAsync-Multi");
                }
            }

            return videos;
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
            catch (Exception ex) when (ex.Message.Contains("parameter has disabled comments"))
            {
                Log.Warn($"無法檢測是否為會限影片: {videoId}");
                Log.Warn($"已關閉留言");
                return false;
            }
            catch (Exception ex) when (ex.Message.ToLower().Contains("notfound"))
            {
                IsDelLive = true;
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn($"無法檢測是否為會限影片: {videoId}");
                Log.Warn(ex.Message);
                return false;
            }

            return false;
        }

        public enum CheckResult
        {
            Ok,
            Redirect,
            CookieFileNotFound,
            CookieFileError,
            OtherError,
        }

        public static async Task<CheckResult> CheckYTCookieAsync(string cookieFilePath)
        {
            if (!File.Exists(cookieFilePath))
                return CheckResult.CookieFileNotFound;

            HttpClientHandler handler = new()
            {
                AllowAutoRedirect = false,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };

            foreach (var item in File.ReadAllLines(cookieFilePath))
            {
                if (!item.StartsWith(".youtube.com"))
                    continue;

                try
                {
                    string[] cookie = item.Split('\t');
                    handler.CookieContainer.Add(new Cookie(cookie[5], cookie[6], cookie[2], cookie[0]));
                }
                catch { }
            }

            if (handler.CookieContainer.Count < 1)
                return CheckResult.CookieFileError;

            using HttpClient client = new(handler, true);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/118.0.0.0 Safari/537.36");

            try
            {
                var respone = await client.GetAsync("https://www.youtube.com/paid_memberships");
                if (respone.Headers.TryGetValues("Location", out var values))
                    return CheckResult.Redirect;

                respone.EnsureSuccessStatusCode();
                return CheckResult.Ok;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return CheckResult.OtherError;
            }
        }
    }

    public static class ProcessUtils
    {
        [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
        private static extern int sys_kill(int pid, int sig);

        public static void Kill(this Process process, Signum sig)
        {
            if (OperatingSystem.IsWindows())
            {
                process.Kill();
            }
            else
            {
                sys_kill(process.Id, (int)sig);
            }
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