using System;
using System.Threading;

namespace Nethermind.Core
{
    public class NullLogger : ILogger
    {
        private static NullLogger _instance;

        public static NullLogger Instance
        {
            get { return LazyInitializer.EnsureInitialized(ref _instance, () => new NullLogger()); }
        }

        public void Log(string text)
        {
        }

        public void Debug(string text)
        {
        }

        public void Error(string text, Exception ex = null)
        {
        }
    }
}