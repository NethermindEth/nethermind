// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.TxPool
{
    /// <remarks>
    /// Extension contract: derive from <c>ChainHeadInfoProvider</c>, which is public and non-sealed, and added
    /// head facts arrive tracked. Implementing this interface directly opts into a compile error per addition —
    /// no member here carries a default body, because a stale or zero head fact is a silent wrong answer.
    /// </remarks>
    public interface IChainHeadInfoProvider
    {
        IChainHeadSpecProvider SpecProvider { get; }

        IReadOnlyStateProvider ReadOnlyStateProvider { get; }

        /// <summary>The canonical head header together with a state view bound to that same header.</summary>
        /// <returns><c>false</c> when the chain has no head yet.</returns>
        /// <remarks><see cref="ReadOnlyStateProvider"/> resolves the head on every read, so a header read beside it
        /// can belong to a different head. A check that must hold the header and the state it reads to one head
        /// takes both from here.</remarks>
        bool TryGetHeadState([NotNullWhen(true)] out BlockHeader? head, [NotNullWhen(true)] out IReadOnlyStateProvider? state);

        /// <summary>
        /// Number of the last block moved onto the canonical chain.
        /// </summary>
        /// <remarks>
        /// Seeded from the processed head, then tracks every block moved onto the main chain. The downloader moves
        /// blocks across without processing them and that raises the same event, so in that mode this follows the
        /// downloaded chain rather than the processed head.
        /// </remarks>
        ulong HeadNumber { get; }

        /// <summary>Timestamp (Unix seconds) of the current chain head.</summary>
        ulong HeadTimestamp { get; }

        ulong? BlockGasLimit { get; }

        UInt256 CurrentBaseFee { get; }

        /// <summary>Base fee per gas (wei) of the block that follows the current chain head.</summary>
        /// <remarks>
        /// Derived from the head header under the spec of the child block, which is resolved at the head's number plus
        /// one and the head's own timestamp. Zero while that spec has EIP-1559 disabled, and zero until the first head
        /// facts are read.
        /// </remarks>
        UInt256 NextBaseFee { get; }

        public UInt256 CurrentFeePerBlobGas { get; }

        ProofVersion CurrentProofVersion { get; }

        bool IsSyncing { get; }
        bool IsProcessingBlock { get; }

        /// <summary>True while this node's block producer executes a block it is building.</summary>
        bool IsBuildingBlock { get; }

        event EventHandler<BlockReplacementEventArgs> HeadChanged;
    }
}
