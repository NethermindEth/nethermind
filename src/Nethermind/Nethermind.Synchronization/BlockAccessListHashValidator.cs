// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization;

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

        errorMessage = null;
        return true;
    }
}
