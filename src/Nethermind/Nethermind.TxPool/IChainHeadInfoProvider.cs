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
        /// Number of the last block moved onto the canonical chain.
        /// </summary>
        /// <remarks>
        /// Seeded from the processed head, then tracks every block moved onto the main chain. The downloader moves
        /// blocks across without processing them and that raises the same event, so in that mode this follows the
        /// downloaded chain rather than the processed head.
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
