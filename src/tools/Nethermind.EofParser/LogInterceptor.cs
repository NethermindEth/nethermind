// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Logging;
public static class LogInterceptor
{
    public static StringBuilder ContentBuilder { get; set; } = new StringBuilder();
    public static string Content => ContentBuilder.ToString();
    public static void Clear()
    {
        LogInterceptor.ContentBuilder.Clear();
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
            LogInterceptor.ContentBuilder.Append($"t: {message}\n\t");
        }
    }

    void ILogger.Debug(string text) 
    {
        if (IsDebug)
        {
            LogInterceptor.ContentBuilder.Append($"d: {text}\n\t");
        }
    }

    void ILogger.Error(string text, Exception ex) 
    {
        if (IsError)
        {
            LogInterceptor.ContentBuilder.Append($"e: {text}\n\t");
        }
    }

    void ILogger.Info(string text) 
    {
        if (IsInfo)
        {
            LogInterceptor.ContentBuilder.Append($"i: {text}\n\t");
        }
    }

    void ILogger.Warn(string text) 
    {
        if (IsWarn)
        {
            LogInterceptor.ContentBuilder.Append($"w: {text}\n\t");
        }
    }
}
