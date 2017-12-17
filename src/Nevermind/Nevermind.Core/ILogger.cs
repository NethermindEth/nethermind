using System;

namespace Nevermind.Core
{
    public interface ILogger
    {
        void Log(string text);
        void Debug(string text);
        void Error(string text, Exception ex = null);
    }
}