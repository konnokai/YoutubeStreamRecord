﻿using CommandLine;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using System;
using System.Reflection;

namespace YoutubeStreamRecord
{
    public static class Program
    {
        public static string VERSION => GetLinkerTime(Assembly.GetEntryAssembly());
        public enum Status { Ready, Deleted, IsClose, IsChatRoom, IsChangeTime };
        public enum ResultType { Once, Sub, Error, None }

        static void Main(string[] args)
        {
            Log.Info($"建置版本: {VERSION}");
            Log.Info($"執行參數: {string.Join(' ', args)}");
            Utility.BotConfig.InitBotConfig();

            // https://stackoverflow.com/a/52029759
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.CancelKeyPress += (sender, e) =>
            {
                Log.Info("Console.CancelKeyPress");
                Utility.IsClose = true;
                e.Cancel = true;
            };

            Utility.YouTube = new YouTubeService(new BaseClientService.Initializer
            {
                ApplicationName = "Bot",
                ApiKey = Utility.BotConfig.GoogleApiKey,
            });

            var result = Parser.Default.ParseArguments<OnceOptions, OnceOnDockerOptions, SubOptions>(args)
                .MapResult(
                (OnceOptions options) => Record.StartRecord(options.VideolId, options.OutputPath, options.TempPath, options.UnarchivedOutputPath, options.MemberOnlyOutputPath, options.DisableRedis, options.DisableLiveFromStart, options.DontSendStartMessage).Result,
                (OnceOnDockerOptions options) => Record.StartRecord(options.VideolId, "/output", "/temp_path", "/unarchived", "/member_only", options.DisableRedis, options.DisableLiveFromStart, options.DontSendStartMessage).Result,
                (SubOptions options) => Subscribe.SubRecord(options.OutputPath, options.TempPath, options.UnarchivedOutputPath, options.MemberOnlyOutputPath, options.AutoDeleteArchived, options.DisableLiveFromStart).Result,
                Error => ResultType.None);

#if DEBUG
            if (result == ResultType.Error || result == ResultType.None)
            {
                Console.WriteLine($"({result}) Press any key to exit...");
                Console.ReadKey();
            }
#else
            if (Utility.InDocker && result == ResultType.Error)
                Environment.Exit(3);

            Environment.Exit(0);
#endif
        }

        public static string GetLinkerTime(Assembly assembly)
        {
            const string BuildVersionMetadataPrefix = "+build";

            var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (attribute?.InformationalVersion != null)
            {
                var value = attribute.InformationalVersion;
                var index = value.IndexOf(BuildVersionMetadataPrefix);
                if (index > 0)
                {
                    value = value[(index + BuildVersionMetadataPrefix.Length)..];
                    return value;
                }
            }
            return default;
        }

        public class RequiredOptions
        {
            [Option('o', "output", Required = true, HelpText = "輸出路徑")]
            public string OutputPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;

            [Option('t', "temp-path", Required = false, HelpText = "暫存路徑")]
            public string TempPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;

            [Option('u', "unarchived-output", Required = true, HelpText = "刪檔直播輸出路徑")]
            public string UnarchivedOutputPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;

            [Option('m', "member-only-output", Required = true, HelpText = "會限直播輸出路徑")]
            public string MemberOnlyOutputPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;

            [Option('s', "disable-live-from-start", Required = false, HelpText = "不從直播開頭錄影，如錄影環境無SSD且須大量同時錄影請開啟本選項")]
            public bool DisableLiveFromStart { get; set; } = false;
        }

        [Verb("once", HelpText = "單次錄影")]
        public class OnceOptions : RequiredOptions
        {
            [Value(0, Required = true, HelpText = "直播Id (需為11字元，如 Id 內有 '-' 請用 '@' 替換)")]
            public string VideolId { get; set; }

            [Option('d', "disable-redis", Required = false, HelpText = "不使用Redis")]
            public bool DisableRedis { get; set; } = false;

            [Option("dont-send-start-message", Required = false, HelpText = "不發送直播開始通知")]
            public bool DontSendStartMessage { get; set; } = false;
        }

        [Verb("onceondocker", HelpText = "在 Docker 環境內單次錄影")]
        public class OnceOnDockerOptions
        {
            [Value(0, Required = true, HelpText = "直播Id (需為11字元，如 Id 內有 '-' 請用 '@' 替換)")]
            public string VideolId { get; set; }

            [Option('d', "disable-redis", Required = false, HelpText = "不使用Redis")]
            public bool DisableRedis { get; set; } = false;

            [Option('s', "disable-live-from-start", Required = false, HelpText = "不從直播開頭錄影，如錄影環境無SSD且須大量同時錄影請開啟本選項")]
            public bool DisableLiveFromStart { get; set; } = false;

            [Option("dont-send-start-message", Required = false, HelpText = "不發送直播開始通知")]
            public bool DontSendStartMessage { get; set; } = false;
        }

        [Verb("sub", HelpText = "訂閱式錄影，此模式需要搭配特定軟體使用，請勿使用")]
        public class SubOptions : RequiredOptions
        {
            [Option('d', "audo-delete", Required = false, HelpText = "自動刪除超過14天的存檔", Default = false)]
            public bool AutoDeleteArchived { get; set; }
        }
    }
}
