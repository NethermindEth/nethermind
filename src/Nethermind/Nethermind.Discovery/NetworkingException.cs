using System;

namespace Nethermind.Discovery
{
    public class NetworkingException : Exception
    {
        public NetworkingException(string message)
            : base(message)
        {
        }
    }
}