using System;

namespace Nevermind.Evm.Abi
{
    public class AbiException : Exception
    {
        public AbiException(string message) : base(message)
        {
        }
    }
}