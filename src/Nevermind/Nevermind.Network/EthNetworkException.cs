using System;

namespace Nevermind.Network
{
    public class EthNetworkException : Exception
    {
        public EthNetworkException(string message)
            : base(message)
        {
        }
    }
}