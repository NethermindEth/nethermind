using System.Numerics;

namespace Nevermind.Core
{
    public class Signature
    {
        public BigInteger R { get; set; }
        public BigInteger S { get; set; }
        public BigInteger V { get; set; }
    }
}