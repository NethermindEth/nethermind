using System;
using System.Threading;

namespace Nethermind.EvmPlayground
{
    internal class NullLogger : ILogger
    {
        private static NullLogger _instance;

        private NullLogger()
        {
        }

        public static NullLogger Instance
        {
            get { return LazyInitializer.EnsureInitialized(ref _instance, () => new NullLogger()); }
        }

        public void Info(string text)
        {
        }

        public void Warn(string text)
        {
        }

        public void Error(string text, Exception ex = null)
        {
        }

        public void Debug(string text)
        {
        }

        public void Trace(string text)
        {
        }

        public void Log(string text)
        {
        }
    }
}