using System;

namespace Nevermind.Core.Encoding
{
    public class RlpException : Exception
    {
        public RlpException(string message)
            : base(message)
        {
        }
    }
}