using System;

namespace Nethermind.AuRa
{
    public class AuRaException : Exception
    {
        protected AuRaException()
        {
        }

        public AuRaException(string message) : base(message)
        {
            
        }
        
        public AuRaException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}