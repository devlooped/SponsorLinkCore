﻿using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Devlooped;

static class Tracing
{
    public static void Trace(string message, object? value, [CallerArgumentExpression("value")] string? expression = null, [CallerFilePath] string? filePath = null, [CallerLineNumber] int lineNumber = 0)
        => Trace($"{message}: {value} ({expression})", filePath, lineNumber);

    public static void Trace(object? value, [CallerArgumentExpression("value")] string? expression = null, [CallerFilePath] string? filePath = null, [CallerLineNumber] int lineNumber = 0)
        => Trace($"{value} ({expression})", filePath, lineNumber);

    public static void Trace([CallerMemberName] string? message = null, [CallerFilePath] string? filePath = null, [CallerLineNumber] int lineNumber = 0)
    {
        var trace = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPONSORLINK_TRACE"));
#if DEBUG
        trace = true;
#endif

        if (!trace)
            return;

        var line = new StringBuilder()
            .Append($"[{DateTime.Now:O}]")
            .Append($"[{Process.GetCurrentProcess().ProcessName}:{Process.GetCurrentProcess().Id}]")
            .Append($" {message} ")
            .AppendLine($" -> {filePath}({lineNumber})")
            .ToString();
        
        var dir = Environment.ExpandEnvironmentVariables(@"%TEMP%\SponsorLink");
        Directory.CreateDirectory(dir);

        while (true)
        {
            try
            {
                File.AppendAllText(Path.Combine(dir, "log.txt"), line);
                return;
            }
            catch (IOException) { }
        }
    }
}
