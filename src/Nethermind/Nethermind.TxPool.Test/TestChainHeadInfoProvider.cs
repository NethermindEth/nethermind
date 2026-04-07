// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.TxPool.Test;

/// <summary>
/// A minimal IChainHeadInfoProvider implementation for testing that avoids NSubstitute's
/// static state issues when running tests in parallel.
/// </summary>
internal class TestChainHeadInfoProvider : IChainHeadInfoProvider
{
    public IChainHeadSpecProvider SpecProvider { get; set; } = null!;
    public IReadOnlyStateProvider ReadOnlyStateProvider { get; set; } = null!;
    public long HeadNumber { get; set; }
    public long? BlockGasLimit { get; set; } = 30_000_000;
    public UInt256 CurrentBaseFee { get; set; }
    public UInt256 CurrentFeePerBlobGas { get; set; }
    public ProofVersion CurrentProofVersion { get; set; }
    public bool IsSyncing { get; set; }
    public bool IsProcessingBlock { get; set; }
    public event EventHandler<BlockReplacementEventArgs>? HeadChanged;

    public void RaiseHeadChanged(BlockReplacementEventArgs args)
    {
        HeadChanged?.Invoke(this, args);
    }
}
