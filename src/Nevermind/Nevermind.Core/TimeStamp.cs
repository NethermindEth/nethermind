using System;
using System.Numerics;

namespace Nevermind.Core
{
    public static class Timestamp
    {
        public static BigInteger UtcNow
        {
            get
            {
                ulong timestamp = (ulong) DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
                return new BigInteger(timestamp);
            }
        }
    }
}