using System;

namespace Nevermind.Core
{
    public class ConsoleLogger : ILogger
    {
        public void Log(string text)
        {
            Console.WriteLine(text);
        }
    }
}