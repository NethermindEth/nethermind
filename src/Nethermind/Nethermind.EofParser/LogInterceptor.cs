// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Logging;
public static class LogSink
{
    public static StringBuilder ContentBuilder { get; set; } = new StringBuilder();
    public static string Content => ContentBuilder.ToString();
    public static void Clear()
    {
        LogSink.ContentBuilder.Clear();
    }
}
public class InMemoryLogger : ILogger
{
    public bool IsDebug { get; set; }
    public bool IsInfo { get; set; }
    public bool IsWarn { get; set; }
    public bool IsError { get; set; }
    public bool IsTrace { get; set; }


    public void Trace(string message)
    {
        if (IsTrace)
        {
            LogSink.ContentBuilder.Append($"{message}\n\t");
        }
    }

    void ILogger.Debug(string text) => throw new NotImplementedException();

    void ILogger.Error(string text, Exception ex) => throw new NotImplementedException();

    void ILogger.Info(string text) => throw new NotImplementedException();

    void ILogger.Warn(string text) => throw new NotImplementedException();
}
