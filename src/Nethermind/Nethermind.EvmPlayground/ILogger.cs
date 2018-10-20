using System;

namespace Nethermind.EvmPlayground
{
    public interface ILogger
    {
        void Info(string text);
        void Warn(string text);
        void Error(string text, Exception ex = null);
    }
}