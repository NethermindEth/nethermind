using System;
using System.IO;
using System.Text;
using Nevermind.Core;

namespace Nevermind.Blockchain.Test.Runner
{
    internal class FileLogger : ILogger
    {
        private readonly string _filePath;

        public FileLogger(string filePath)
        {
            _filePath = filePath;
        }

        private readonly StringBuilder _buffer = new StringBuilder();

        public void Log(string text)
        {
            try
            {
                _buffer.AppendLine(text);
                if (_buffer.Length > 1024 * 1024)
                {
                    Flush();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        public void Debug(string text)
        {
            Log(text);
        }

        public void Error(string text, Exception ex = null)
        {
            Log(ex != null ? $"{text}, Exception: {ex}" : text);
        }

        public void Flush()
        {
            File.AppendAllText(_filePath, _buffer.ToString());
            _buffer.Clear();
        }
    }
}