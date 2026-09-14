// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.TxPool
{
    public interface IChainHeadInfoProvider
    {
        IChainHeadSpecProvider SpecProvider { get; }

        IReadOnlyStateProvider ReadOnlyStateProvider { get; }

        /// <summary>
        /// Number of the last block on the canonical chain that this node has processed.
        /// </summary>
        /// <remarks>
        /// This is the processed head, not the best downloaded block, so it stays behind the chain tip while syncing.
        /// </remarks>
        ulong HeadNumber { get; }

        ulong? BlockGasLimit { get; }

        UInt256 CurrentBaseFee { get; }

        public UInt256 CurrentFeePerBlobGas { get; }

        ProofVersion CurrentProofVersion { get; }

        bool IsSyncing { get; }
        bool IsProcessingBlock { get; }

        event EventHandler<BlockReplacementEventArgs> HeadChanged;
    }
}
