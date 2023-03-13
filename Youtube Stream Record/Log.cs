using System;
using System.Diagnostics;

public static class Log
{
    static object _lock = new object();

    enum LogType { Verb, Stream, Info, Warn, Error }

    public static void YouTubeInfo(string text, bool newLine = true)
    {
        lock (_lock)
        {
            FormatColorWrite(text, ConsoleColor.White, newLine);
        }
    }

    public static void Info(string text, bool newLine = true)
    {
        lock (_lock)
        {
            FormatColorWrite(text, ConsoleColor.DarkYellow, newLine);
        }
    }

    public static void Warn(string text, bool newLine = true)
    {
        lock (_lock)
        {
            FormatColorWrite(text, ConsoleColor.DarkMagenta, newLine);
        }
    }

    public static void Debug(string text, bool newLine = true)
    {
        if (!Debugger.IsAttached)
            return;

        lock (_lock)
        {
            FormatColorWrite(text, ConsoleColor.Cyan, newLine);
        }
    }

    public static void Error(string text, bool newLine = true)
    {
        lock (_lock)
        {
            FormatColorWrite(text, ConsoleColor.DarkRed, newLine);
        }
    }

    public static void Error(Exception ex, string text, bool newLine = true)
    {
        lock (_lock)
        {
            FormatColorWrite(text, ConsoleColor.DarkRed, newLine);
            FormatColorWrite(ex.ToString(), ConsoleColor.DarkRed, newLine);
        }
    }

    public static void FormatColorWrite(string text, ConsoleColor consoleColor = ConsoleColor.Gray, bool newLine = true)
    {
        lock (_lock)
        {
            text = $"[{DateTime.Now}] {text}";
            Console.ForegroundColor = consoleColor;
            if (newLine) Console.WriteLine(text);
            else Console.Write(text);
            Console.ForegroundColor = ConsoleColor.Gray;
        }
    }
}