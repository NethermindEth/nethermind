using System;
using Nethermind.Core;

namespace Nethermind.Runner
{
    public class NlogLogger : ILogger
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        public void Log(string text)
        {
            Logger.Info(text);
        }

        public void Debug(string text)
        {
            Logger.Debug(text);
        }

        public void Error(string text, Exception ex = null)
        {
            Logger.Error(ex, text);
        }
    }
}