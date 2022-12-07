using Nethermind.Dirichlet.Numerics;
using CoreUInt256 = Nethermind.Int256.UInt256;

namespace Nethermind.Merkleization
{
    static class UInt256Extensions
    {
        public static UInt256 ToDirichlet(this CoreUInt256 coreInt)
        {
            return new UInt256(coreInt.u0, coreInt.u1, coreInt.u2, coreInt.u3);
        }
    }
}
