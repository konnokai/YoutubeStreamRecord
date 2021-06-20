using System;
public static class Log
{
    public static void Info(string text, bool newLine = true)
    {
        FormatColorWrite(text, ConsoleColor.DarkYellow, newLine);
    }

    public static void Warn(string text, bool newLine = true)
    {
        FormatColorWrite(text, ConsoleColor.DarkMagenta, newLine);
    }

    public static void Error(string text, bool newLine = true)
    {
        FormatColorWrite(text, ConsoleColor.DarkRed, newLine);
    }

    public static void Debug(string text, bool newLine = true)
    {
#if DEBUG
        FormatColorWrite($"[Debug] {text}", ConsoleColor.Green, newLine);
#endif
    }

    public static void FormatColorWrite(string text, ConsoleColor consoleColor = ConsoleColor.Gray, bool newLine = true)
    {
        text = DateTime.Now.ToString() + " " + text;
        Console.ForegroundColor = consoleColor;
        if (newLine) Console.WriteLine(text);
        else Console.Write(text);
        Console.ForegroundColor = ConsoleColor.Gray;
    }
}