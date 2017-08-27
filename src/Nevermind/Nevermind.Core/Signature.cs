using System;

namespace Nevermind.Core
{
    public class Signature
    {
        public byte[] Bytes { get; }
        public int RecoveryId { get; }

        internal Signature(byte[] bytes, int recoveryId)
        {
            if (bytes.Length != 64)
            {
                throw new ArgumentException();
            }

            Bytes = bytes;
            RecoveryId = recoveryId;
        }
    }
}