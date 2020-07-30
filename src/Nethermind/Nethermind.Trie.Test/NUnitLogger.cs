using System;
using System.Threading;
using Nethermind.Core.Test;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Trie.Test
{
    public class NUnitLogger : ILogger
    {
        private readonly LogLevel _logLevel;

        public NUnitLogger(LogLevel logLevel)
        {
            _logLevel = logLevel;
        }

        private static int _keepOrdering = 0;
        
        private void Log(string text)
        {
            TestContext.Out.WriteLine($"{Interlocked.Increment(ref _keepOrdering)}|{DateTime.Now:HH:mm:ss.fff} [{Thread.CurrentThread.ManagedThreadId}] {text}");
            TestContext.Out.Flush();
        }

        public void Info(string text)
        {
            Log(text);
        }

        public void Warn(string text)
        {
            Log(text);
        }

        public void Debug(string text)
        {
            Log(text);
        }

        public void Trace(string text)
        {
            Log(text);
        }

        public void Error(string text, Exception ex = null)
        {
            Log(ex != null ? $"{text}, Exception: {ex}" : text);
        }

        public bool IsInfo => (int) _logLevel >= 2;
        public bool IsWarn => (int) _logLevel >= 1;
        public bool IsDebug => (int) _logLevel >= 3;
        public bool IsTrace => (int) _logLevel >= 4;
        public bool IsError => true;
    }
}