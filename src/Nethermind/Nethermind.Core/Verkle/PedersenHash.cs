using System;
using System.Buffers;
using System.Buffers.Binary;
using Nethermind.Core.Extensions;
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
        Span<byte> data = stackalloc byte[64];
        address.CopyTo(data[(address.Length == 20 ? 12 : 0)..]);
        treeIndex.ToLittleEndian(data[32..]);

        var hash = new byte[32];
        VerkleCrypto.PedersenHashFlat(data, hash);
        return hash;
    }
}
