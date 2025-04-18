﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System;
using System.Collections.Generic;
using System.IO;

public static class Logger
{
    public enum LogLevel
    {
        Trace,
        Debug,
        DebugError,
        Info,
        Warn,
        Error,
        Fatal
    }

    public class OutputConfig
    {
        public Action<string> Output;
        public LogLevel MinimumLevel;
        public bool PrependDate;
        public bool PrependLogLevel;
        public bool IsFileOutput;
        public bool IsConsoleOutput;
        public bool UseConsoleColors; // Only applicable for console output
    }

    private static readonly List<OutputConfig> OutputConfigs = new();

    private static readonly object LockObject = new ();

    public static void SetupExceptionHandling()
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            var exception = (Exception)args.ExceptionObject;
            LogNonConsole(LogLevel.Fatal, $"Unhandled exception. {args.ExceptionObject}: {exception.Message}\n{exception.StackTrace}");
        };
    }

    private static void AddOutput(Action<string> output, LogLevel minimumLevel = LogLevel.Info, bool prependDate = true, 
        bool prependLogLevel = true, bool isFileOutput = false)
    {
        lock (LockObject)
        {
            OutputConfigs.Add(new OutputConfig
            {
                Output = output,
                PrependDate = prependDate,
                PrependLogLevel = prependLogLevel,
                UseConsoleColors = false,
                MinimumLevel = minimumLevel,
                IsFileOutput = isFileOutput
            });
        }
    }

    public static void AddConsole(LogLevel minimumLevel = LogLevel.Info, bool useColors = true, bool prependDate = false, bool prependLogLevel = false)
    {
        AddOutput(Console.WriteLine, minimumLevel, prependDate, prependLogLevel);
        var consoleConfig = OutputConfigs[^1];
        consoleConfig.UseConsoleColors = useColors;
        consoleConfig.IsConsoleOutput = true;
    }

    public static void SetConsoleLogLevel(LogLevel logLevel)
    {
        OutputConfigs.First(x => x.Output == Console.WriteLine).MinimumLevel = logLevel;
    }

    public static void AddFile(string filePath, LogLevel minimumLevel = LogLevel.Debug, bool prependDate = true, bool prependLogLevel = true)
    {
        AddOutput(message => File.AppendAllText(filePath, message + '\n'), minimumLevel, prependDate, prependLogLevel, isFileOutput: true);
    }

    public static void AddOrReplaceFile(string filePath, LogLevel minimumLevel = LogLevel.Debug, bool prependDate = true, bool prependLogLevel = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath));
        OutputConfigs.RemoveAll(config => config.IsFileOutput);
        AddOutput(message => File.AppendAllText(filePath, message + '\n'), minimumLevel, prependDate, prependLogLevel, isFileOutput: true);
    }

    public static void Log(LogLevel level, string message, IEnumerable<OutputConfig>? outputs = null)
    {
        if (outputs == null)
            outputs = OutputConfigs;

        lock (LockObject)
        {
            foreach (var config in outputs)
            {
                if (level < config.MinimumLevel)
                    continue;

                if (!config.IsConsoleOutput) message = message.TrimStart();
                string logEntry = BuildLogEntry(level, message, config.PrependDate, config.PrependLogLevel);

                if (config.IsConsoleOutput && config.UseConsoleColors)
                {
                    SetConsoleColor(level);
                    config.Output(logEntry);
                    Console.ResetColor();
                }
                else
                {
                    config.Output(logEntry);
                }
            }
        }
    }

    public static void LogNonConsole(LogLevel level, string message)
    {
        Log(level, message, OutputConfigs.Where(x => x.Output != Console.WriteLine));
    }

    private static string BuildLogEntry(LogLevel level, string message, bool prependDate, bool prependLogLevel)
    {
        var parts = new List<string>();

        if (prependDate)
        {
            parts.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }

        if (prependLogLevel)
        {
            parts.Add($"[{level}]");
        }

        parts.Add(message);

        return string.Join(" ", parts);
    }

    private static void SetConsoleColor(LogLevel level)
    {
        Console.ForegroundColor = level switch
        {
            LogLevel.Error or LogLevel.Fatal => ConsoleColor.Red,
            LogLevel.Warn or LogLevel.DebugError => ConsoleColor.DarkYellow,
            _ => ConsoleColor.Gray,
        };
    }

    public static void Trace(string message) => Log(LogLevel.Trace, message);
    public static void Debug(string message) => Log(LogLevel.Debug, message);
    public static void DebugError(string message) => Log(LogLevel.DebugError, message);
    public static void Info(string message) => Log(LogLevel.Info, message);
    public static void Warn(string message) => Log(LogLevel.Warn, message);
    public static void Error(string message) => Log(LogLevel.Error, message);
    public static void Fatal(string message) => Log(LogLevel.Fatal, message);
}
