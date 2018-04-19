using System;
using System.Threading;

namespace Nethermind.Core
{
    public class SimpleConsoleLogger : ILogger
    {
        public void Log(string text)
        {
            Console.WriteLine($"{DateTime.Now.ToLongTimeString()} [{Thread.CurrentThread.ManagedThreadId}] {text}");
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

        public bool IsInfoEnabled => true;
        public bool IsWarnEnabled => true;
        public bool IsDebugEnabled => true;
        public bool IsTraceEnabled => true;
        public bool IsErrorEnabled => true;
    }
}