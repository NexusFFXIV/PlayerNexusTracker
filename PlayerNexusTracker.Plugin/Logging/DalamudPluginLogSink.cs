using System;
using Dalamud.Plugin.Services;
using NexusKit.Core.Logging;

namespace PlayerNexusTracker.Logging;

/// <summary>
/// Bridges <see cref="IPluginLogSink"/> (framework-side, Dalamud-free abstraction)
/// to Dalamud's <see cref="IPluginLog"/>. Registered once by <c>Plugin.LoadAsync</c>
/// so the framework's <c>ILogger&lt;T&gt;</c> pipeline lands in Dalamud's
/// <c>/xllog</c>. One method per log level; no formatting on this side.
/// </summary>
public sealed class DalamudPluginLogSink : IPluginLogSink
{
    private readonly IPluginLog mPluginLog;

    public DalamudPluginLogSink(IPluginLog pluginLog)
    {
        mPluginLog = pluginLog;
    }

    public void Verbose(string message, Exception? exception = null)
    {
        if (exception is null) mPluginLog.Verbose(message);
        else mPluginLog.Verbose(exception, message);
    }

    public void Debug(string message, Exception? exception = null)
    {
        if (exception is null) mPluginLog.Debug(message);
        else mPluginLog.Debug(exception, message);
    }

    public void Information(string message, Exception? exception = null)
    {
        if (exception is null) mPluginLog.Information(message);
        else mPluginLog.Information(exception, message);
    }

    public void Warning(string message, Exception? exception = null)
    {
        if (exception is null) mPluginLog.Warning(message);
        else mPluginLog.Warning(exception, message);
    }

    public void Error(string message, Exception? exception = null)
    {
        if (exception is null) mPluginLog.Error(message);
        else mPluginLog.Error(exception, message);
    }

    public void Fatal(string message, Exception? exception = null)
    {
        if (exception is null) mPluginLog.Fatal(message);
        else mPluginLog.Fatal(exception, message);
    }
}
