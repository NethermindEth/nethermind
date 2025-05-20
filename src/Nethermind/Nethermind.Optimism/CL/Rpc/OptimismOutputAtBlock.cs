// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

