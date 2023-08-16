using System;
using System.Buffers.Binary;
using Nethermind.Int256;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Core.Verkle;

public static class PedersenHash
{
    public static byte[] Hash(UInt256[] inputElements)
    {
        int inputLength = inputElements.Length;
        FrE[] pedersenVec = new FrE[1 + 2 * inputLength];
        pedersenVec[0] = FrE.SetElement((ulong)(2 + 256 * inputLength * 32));

        for (int i = 0; i < inputElements.Length; i++)
        {
            pedersenVec[2 * i + 1] = FrE.SetElement(inputElements[i].u0, inputElements[i].u1);
            pedersenVec[2 * i + 2] = FrE.SetElement(inputElements[i].u2, inputElements[i].u3);
        }
        CRS crs = CRS.Instance;

        Banderwagon res = Banderwagon.Identity;
        for (int i = 0; i < pedersenVec.Length; i++)
        {
            res += crs.BasisG[i] * pedersenVec[i];
        }

        return res.ToBytesLittleEndian();
    }

    public static void ComputeHashBytesToSpan(ReadOnlySpan<byte> address20, UInt256 treeIndex, Span<byte> output)
    {
        Hash(address20, treeIndex).CopyTo(output);
    }

    public static byte[] ComputeHashBytes(ReadOnlySpan<byte> address20, UInt256 treeIndex) =>
        Hash(address20, treeIndex);

    public static byte[] Hash(ReadOnlySpan<byte> address20, UInt256 treeIndex)
    {
        ulong u0, u1, u2, u3;
        if (address20.Length == 32)
        {
            UInt256 temp = new UInt256(address20);
            u0 = temp.u0;
            u1 = temp.u1;
            u2 = temp.u2;
            u3 = temp.u3;
        }
        else
        {
            u0 = 0;
            Span<byte> u1Bytes = new byte[8];
            address20[..4].CopyTo(u1Bytes[4..]);
            u1 = BinaryPrimitives.ReadUInt64LittleEndian(u1Bytes);
            u2 = BinaryPrimitives.ReadUInt64LittleEndian(address20.Slice(4, 8));
            u3 = BinaryPrimitives.ReadUInt64LittleEndian(address20.Slice(12, 8));
        }


        CRS crs = CRS.Instance;

        Banderwagon res = crs.BasisG[0] * FrE.SetElement(2 + 256 * 64)
                          + crs.BasisG[1] * FrE.SetElement(u0, u1)
                          + crs.BasisG[2] * FrE.SetElement(u2, u3)
                          + crs.BasisG[3] * FrE.SetElement(treeIndex.u0, treeIndex.u1)
                          + crs.BasisG[4] * FrE.SetElement(treeIndex.u2, treeIndex.u3);

        return res.ToBytesLittleEndian();
    }
}
