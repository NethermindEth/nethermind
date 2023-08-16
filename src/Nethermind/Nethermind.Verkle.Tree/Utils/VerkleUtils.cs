using System.Numerics;
using Nethermind.Int256;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Verkle.Tree.Utils
{
    public struct LeafUpdateDelta
    {
        public Banderwagon? DeltaC1 { get; private set; }
        public Banderwagon? DeltaC2 { get; private set; }

        public LeafUpdateDelta()
        {
            DeltaC1 = null;
            DeltaC2 = null;
        }

        public void UpdateDelta(Banderwagon deltaLeafCommitment, byte index)
        {
            if (index < 128)
            {
                if (DeltaC1 is null) DeltaC1 = deltaLeafCommitment;
                else DeltaC1 += deltaLeafCommitment;
            }
            else
            {
                if (DeltaC2 is null) DeltaC2 = deltaLeafCommitment;
                else DeltaC2 += deltaLeafCommitment;
            }
        }
    }

    public static class VerkleUtils
    {
        private static readonly FrE ValueExistsMarker = FrE.SetElement(BigInteger.Pow(2, 128));

        public static (FrE, FrE) BreakValueInLowHigh(byte[]? value)
        {
            if (value is null) return (FrE.Zero, FrE.Zero);
            if (value.Length != 32) throw new ArgumentException();
            UInt256 valueFr = new(value);
            FrE lowFr = FrE.SetElement(valueFr.u0, valueFr.u1) + ValueExistsMarker;
            FrE highFr = FrE.SetElement(valueFr.u2, valueFr.u3);
            return (lowFr, highFr);
        }

        public static (List<byte>, byte?, byte?) GetPathDifference(IEnumerable<byte> existingNodeKey, IEnumerable<byte> newNodeKey)
        {
            List<byte> samePathIndices = new List<byte>();
            foreach ((byte first, byte second) in existingNodeKey.Zip(newNodeKey))
            {
                if (first != second) return (samePathIndices, first, second);
                samePathIndices.Add(first);
            }
            return (samePathIndices, null, null);
        }
    }
}
