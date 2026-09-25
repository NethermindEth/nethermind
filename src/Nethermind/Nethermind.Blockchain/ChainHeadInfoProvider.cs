// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.TxPool;

[assembly: InternalsVisibleTo("Nethermind.TxPool.Test")]

namespace Nethermind.Blockchain
{
    public class ChainHeadInfoProvider : IChainHeadInfoProvider
    {
        private readonly IBlockTree _blockTree;
        // For testing
        public bool HasSynced { private get; init; }

        public ChainHeadInfoProvider(IChainHeadSpecProvider specProvider, IBlockTree blockTree, IStateReader stateReader)
            : this(specProvider, blockTree, new ChainHeadReadOnlyStateProvider(blockTree, stateReader))
        {
        }

        public ChainHeadInfoProvider(IChainHeadSpecProvider specProvider, IBlockTree blockTree, IReadOnlyStateProvider stateProvider)
        {
            SpecProvider = specProvider;
            ReadOnlyStateProvider = stateProvider;
            Block? head = blockTree.Head;
            HeadNumber = head?.Number ?? 0;
            HeadTimestamp = head?.Timestamp ?? 0;
            // Genesis is not a head worth gating on: a node still syncing to the tip would price and bound
            // transactions against it, so the facts below stay at their defaults until the first head change.
            if (head is not null && !head.IsGenesis) ReadHead(head.Header);

            blockTree.BlockAddedToMain += OnHeadChanged;
            _blockTree = blockTree;
        }

        public IChainHeadSpecProvider SpecProvider { get; }

        public IReadOnlyStateProvider ReadOnlyStateProvider { get; }

        public ulong HeadNumber { get; private set; }

        public ulong HeadTimestamp { get; private set; }

        public ulong? BlockGasLimit { get; internal set; }

        public UInt256 CurrentBaseFee { get; private set; }

        public UInt256 CurrentFeePerBlobGas { get; internal set; }

        public ProofVersion CurrentProofVersion { get; private set; }

        public bool IsSyncing
        {
            get
            {
                if (HasSynced)
                {
                    return false;
                }

                (bool isSyncing, _, _) = _blockTree.IsSyncing(maxDistanceForSynced: 16);
                return isSyncing;
            }
        }

        public bool IsProcessingBlock => _blockTree.IsProcessingBlock;

        public event EventHandler<BlockReplacementEventArgs>? HeadChanged;

        private void OnHeadChanged(object? sender, BlockReplacementEventArgs e)
        {
            HeadNumber = e.Block.Number;
            ReadHead(e.Block.Header);
            HeadChanged?.Invoke(sender, e);
        }

        /// <summary>Reads the head-derived facts the transaction pool gates on off <paramref name="header"/>.</summary>
        /// <remarks>Shared with the constructor, so a node that has seen no <see cref="IBlockTree.BlockAddedToMain"/>
        /// yet gates on its own head rather than on the defaults. <see cref="HeadNumber"/> stays with the callers
        /// because it is seeded even for a genesis head, which the facts here are not.</remarks>
        private void ReadHead(BlockHeader header)
        {
            IReleaseSpec spec = SpecProvider.GetSpec(header);
            HeadTimestamp = header.Timestamp;
            BlockGasLimit = header.GasLimit;
            CurrentBaseFee = header.BaseFeePerGas;
            CurrentFeePerBlobGas =
                BlobGasCalculator.TryCalculateFeePerBlobGas(header, spec.BlobBaseFeeUpdateFraction, out UInt256 currentFeePerBlobGas)
                    ? currentFeePerBlobGas
                    : UInt256.Zero;
            CurrentProofVersion = spec.BlobProofVersion;
        }
    }
}
