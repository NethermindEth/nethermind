// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>Portable identity encoding for native validation contract 1; does not authorize execution or signing.</summary>
internal static class RollupXValidationIdentity
{
    private static ReadOnlySpan<byte> Domain => "eez.validation-identity"u8;
    private const ulong Version = 1;

    internal static Hash256 Compute(in Inputs input) => Keccak.Compute(Encode(in input));

    internal static byte[] Encode(in Inputs input)
    {
        int length = Rlp.LengthOf(Domain) + Rlp.LengthOf(Version) + Rlp.LengthOf(input.RollupId) + Rlp.LengthOf(input.ChainId)
            + Rlp.LengthOf(input.SystemAddress) + Rlp.LengthOf(input.Eezl2Address) + Rlp.LengthOf(input.ChainDocumentDigest)
            + Rlp.LengthOf(input.OfflineProfileHash) + Rlp.LengthOf(input.Registry) + Rlp.LengthOf(input.Manager);
        byte[] encoded = new byte[Rlp.LengthOfSequence(length)];
        RlpWriter writer = new(encoded);
        writer.StartSequence(length);
        writer.Encode(Domain);
        writer.Encode(Version);
        writer.Encode(input.RollupId);
        writer.Encode(input.ChainId);
        writer.Encode(input.SystemAddress);
        writer.Encode(input.Eezl2Address);
        writer.Encode(input.ChainDocumentDigest);
        writer.Encode(input.OfflineProfileHash);
        writer.Encode(input.Registry);
        writer.Encode(input.Manager);
        return encoded;
    }

    internal readonly record struct Inputs(ulong RollupId, ulong ChainId, Address SystemAddress, Address Eezl2Address,
        Hash256 ChainDocumentDigest, Hash256 OfflineProfileHash, Address Registry, Address Manager);
}
