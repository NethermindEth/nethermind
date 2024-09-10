using System;
using System.Buffers.Binary;
using Nethermind.Int256;
using Nethermind.Verkle;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Core.Verkle;

public static class PedersenHash
{
    public static byte[] ComputeHashBytes(byte[] address20, UInt256 treeIndex) =>
        HashRust(address20, treeIndex);

    public static byte[] HashRust(byte[] address, UInt256 treeIndex)
    {
        var hash = new byte[32];
        byte[] address32 = new byte[32];
        address.CopyTo(address32, address.Length == 20 ? 12 : 0);
        VerkleCrypto.PedersenHash(address32, treeIndex.ToLittleEndian(), hash);
        return hash;
    }
}
