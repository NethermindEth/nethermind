using System;
using System.Threading;

namespace Nethermind.Core
{
    public class ConsoleLogger : ILogger
    {
        public void Log(string text)
        {
            Console.WriteLine($"{DateTime.Now.ToLongTimeString()} [{Thread.CurrentThread.ManagedThreadId}] {text}");
        }

        public void Debug(string text)
        {
            Log(text);
        }

        public void Error(string text, Exception ex = null)
        {
            Log(ex != null ? $"{text}, Exception: {ex}" : text);
        }
    }
}