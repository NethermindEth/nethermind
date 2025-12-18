// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Optimism.CL;

namespace Nethermind.Optimism.Cl.Rpc;

public sealed record OptimismOutputAtBlock
{
    public required byte[] Version { get; init; }
    public required Hash256 OutputRoot { get; init; }
    public required L2BlockRef BlockRef { get; init; }
    public required Hash256 WithdrawalStorageRoot { get; init; }
    public required Hash256 StateRoot { get; init; }
    public required OptimismSyncStatus Status { get; init; }
}

public sealed record OptimismOutputV0
{
    public static readonly byte[] Version = new byte[32];

    public required Hash256 StateRoot { get; init; }
    public required Hash256 MessagePasserStorageRoot { get; init; }
    public required Hash256 BlockHash { get; init; }

    [SkipLocalsInit]
    public Hash256 Root()
    {
        Span<byte> buffer = stackalloc byte[128];

        Version.CopyTo(buffer);
        StateRoot.Bytes.CopyTo(buffer[32..]);
        MessagePasserStorageRoot.Bytes.CopyTo(buffer[64..]);
        BlockHash.Bytes.CopyTo(buffer[96..]);

        return Keccak.Compute(buffer);
    }
}
