﻿using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using static Devlooped.Tracing;

namespace Devlooped;

static class SessionManager
{
    static readonly string SessionsDirectory = Environment.ExpandEnvironmentVariables($"%TEMP%\\1M5Ot");
    static readonly Timer? timer;

    static SessionManager()
    {
        // TODO: perform some cleanup on the temp dir? We're not taking much space
        // and it's a temp dir that Windows can offer cleanup on already
        //AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
        //{
        //    if (SessionId == null || !Directory.Exists(SessionsDirectory))
        //        return;

        //    var path = Path.Combine(SessionsDirectory, SessionId);
        //    try
        //    {
        //        // Best-effort, it's a temp dir anyway.
        //        Directory.Delete(path, true);
        //    }
        //    catch { }
        //};

        // Rider sets the parent process ID, but the child process lingers anyway, so we must ensure 
        // we exit the process or we'll never re-check regardless of how many times Rider itself is restarted.
        if (Environment.GetEnvironmentVariable("MSBUILD_TASK_PARENT_PROCESS_PID") is string parentId && 
            int.TryParse(parentId, out var processId))
        {
            timer = new Timer(_ =>
            {
                if (Process.GetProcessById(processId) == null)
                    // Ensure we don't linger without a parent process.
                    Process.GetCurrentProcess().Kill();
                else
                    // Otherwise, restart timer.
                    timer?.Change(30000, Timeout.Infinite);
            });

            timer.Change(30000, Timeout.Infinite);
        }
    }

    public static string? SessionId { get; } =
        Environment.GetEnvironmentVariable("ServiceHubLogSessionKey") ??
        RiderSession(Environment.GetEnvironmentVariable("RESHARPER_FUS_SESSION")) ??
        default;

    public static bool IsEditor => IsVisualStudio || IsRider;

    public static bool IsVisualStudio =>
        Environment.GetEnvironmentVariable("ServiceHubLogSessionKey") != null ||
        Environment.GetEnvironmentVariable("VSAPPIDNAME") != null;

    public static bool IsRider =>
        Environment.GetEnvironmentVariable("RESHARPER_FUS_SESSION") != null ||
        Environment.GetEnvironmentVariable("IDEA_INITIAL_DIRECTORY") != null;

    public static bool IsCI =>
        (bool.TryParse(Environment.GetEnvironmentVariable("CI"), out var ci) && ci) || 
        (bool.TryParse(Environment.GetEnvironmentVariable("TF_BUILD"), out ci) && ci) ||
        (bool.TryParse(Environment.GetEnvironmentVariable("TRAVIS"), out ci) && ci) ||
        (bool.TryParse(Environment.GetEnvironmentVariable("BUDDY"), out ci) && ci) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEAMCITY_VERSION")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPVEYOR")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JENKINS_URL")) ||
        Environment.GetEnvironmentVariable("BuildRunner") == "MyGet";

    static string? RiderSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        // Shorten as much as possible while retaining some uniqueness
        return Base62.Encode(BigInteger.Parse(new string(sessionId.Where(char.IsDigit).ToArray())));
    }

    public static void Set(string sponsorable, string product, string project, DiagnosticKind kind)
    {
        // We cannot persist without a session ID.
        if (SessionId == null)
            return;

        var hash = Key(sponsorable, product, project);
        using var mutex = new Mutex(false, $"{SessionId}_{hash}", out _);
        // Only one can write at a time.
        mutex.WaitOne();

        try
        {
            var path = Path.Combine(SessionsDirectory, SessionId, hash);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, ((int)kind).ToString());
            Trace($"Set {SessionId}: {sponsorable}/{product}@{Path.GetFileNameWithoutExtension(project)} = {kind}");
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public static bool TryGet(string sponsorable, string product, string project, out DiagnosticKind kind)
    {
        // If we couldn't determine a session ID, bail.
        // Might have been what happened in https://github.com/devlooped/nugetizer/issues/370
        if (SessionId == null)
        {
            kind = default;
            return false;
        }

        var hash = Key(sponsorable, product, project);
        using var mutex = new Mutex(false, $"{SessionId}_{hash}", out _);

        var path = Path.Combine(SessionsDirectory, SessionId, hash);

        if (Directory.Exists(SessionsDirectory) &&
            File.Exists(path) &&
            File.ReadAllText(path) is string value &&
            Enum.TryParse(value, out kind))
        {
            Trace($"Get {SessionId}: {sponsorable}/{product}@{Path.GetFileNameWithoutExtension(project)} => {kind}");
            return true;
        }

        kind = default;
        Trace($"Get {SessionId}: {sponsorable}/{product}@{Path.GetFileNameWithoutExtension(project)} => ?");
        return false;
    }

    static string Key(string sponsorable, string product, string project)
        => Hashed($"{sponsorable}|{product}|{project}");

    static string Hashed(string key)
    {
        var data = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(key));
        var hash = Base62.Encode(BigInteger.Abs(new BigInteger(data)));
        return hash;
    }
}
