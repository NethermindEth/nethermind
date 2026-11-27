// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Synchronization;

/// <summary>Checks a block access list received from a peer against its header before it is stored.</summary>
/// <remarks>
/// A list that matches the header's commitment must also pass the encoding checks that <c>engine_newPayload</c>
/// applies to a block access list, see <see cref="BlockAccessListDecoder.Validate"/>.
/// </remarks>
internal static class BlockAccessListHashValidator
{
    public static bool Validate(BlockHeader header, ReadOnlySpan<byte> encodedAccessList, out string? errorMessage)
    {
        Hash256? expectedHash = header.BlockAccessListHash;
        if (expectedHash is null)
        {
            errorMessage = "block header is missing block access list hash";
            return false;
        }

        ValueHash256 actualHash = ValueKeccak.Compute(encodedAccessList);
        if (actualHash != expectedHash.ValueHash256)
        {
            errorMessage = $"block access list hash mismatch, expected {expectedHash}, got {actualHash}";
            return false;
        }

        try
        {
            BlockAccessListDecoder.Validate(encodedAccessList);
        }
        catch (RlpReaderException e)
        {
            errorMessage = $"error decoding block access list: {e.Message}";
            return false;
        }

        errorMessage = null;
        return true;
    }
}
