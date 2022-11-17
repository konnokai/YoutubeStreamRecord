using CommandLine;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using System;

namespace Youtube_Stream_Record
{
    public static class Program
    {
        public enum Status { Ready, Deleted, IsClose, IsChatRoom, IsChangeTime };
        public enum ResultType { Loop, Once, Sub, Error, None }

        static void Main(string[] args)
        {
            Utility.BotConfig.InitBotConfig();

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

#if DEBUG
            Console.WriteLine(string.Join(' ', args));
#endif

            var result = Parser.Default.ParseArguments<LoopOptions, OnceOptions, SubOptions>(args)
                .MapResult(
                (LoopOptions lo)  => Record.StartRecord(lo.ChannelId, lo.OutputPath, lo.TempPath, lo.UnarchivedOutputPath, lo.StartStreamLoopTime, lo.CheckNextStreamTime, true, lo.DisableRedis,lo.DisableLiveFromStart).Result,
                (OnceOptions oo) => Record.StartRecord(oo.ChannelId, oo.OutputPath, oo.TempPath, oo.UnarchivedOutputPath, oo.StartStreamLoopTime, oo.CheckNextStreamTime, false, oo.DisableRedis, oo.DisableLiveFromStart).Result,
                (SubOptions so) => Subscribe.SubRecord(so.OutputPath, so.TempPath, so.UnarchivedOutputPath, so.AutoDeleteArchived,so.DisableLiveFromStart).Result,
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
