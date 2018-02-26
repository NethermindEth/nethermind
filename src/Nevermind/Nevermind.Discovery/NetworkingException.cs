using System;

namespace Nevermind.Discovery
{
    public class NetworkingException : Exception
    {
        public NetworkingException(string message)
            : base(message)
        {
        }
    }
}