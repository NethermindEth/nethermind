// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>Portable identity encoding for the native validation contract; does not authorize execution or signing.</summary>
internal static class RollupXValidationIdentity
{
    private static ReadOnlySpan<byte> Domain => "eez.validation-identity"u8;
    internal const ulong Version = 3;

    internal static Hash256 Compute(in Inputs input) => Keccak.Compute(Encode(in input));

    internal static byte[] Encode(in Inputs input)
    {
        ValidateVersion(input.ValidationVersion);
        int length = Rlp.LengthOf(Domain) + Rlp.LengthOf(input.ValidationVersion) + Rlp.LengthOf(input.RollupId) + Rlp.LengthOf(input.ChainId)
            + Rlp.LengthOf(input.SystemAddress) + Rlp.LengthOf(input.Eezl2Address) + Rlp.LengthOf(input.ChainDocumentDigest)
            + Rlp.LengthOf(input.OfflineProfileHash) + Rlp.LengthOf(input.Registry) + Rlp.LengthOf(input.Manager);
        byte[] encoded = new byte[Rlp.LengthOfSequence(length)];
        RlpWriter writer = new(encoded);
        writer.StartSequence(length);
        writer.Encode(Domain);
        writer.Encode(input.ValidationVersion);
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

    internal static void ValidateVersion(ulong version)
    {
        if (version is not (3 or 4 or 6 or 7)) throw new ArgumentOutOfRangeException(nameof(version));
    }

    internal readonly record struct Inputs(ulong RollupId, ulong ChainId, Address SystemAddress, Address Eezl2Address,
        Hash256 ChainDocumentDigest, Hash256 OfflineProfileHash, Address Registry, Address Manager, ulong ValidationVersion = Version);
}
