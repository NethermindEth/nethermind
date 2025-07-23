// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Blockchain.Spec;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
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

        public ChainHeadInfoProvider(ISpecProvider specProvider, IBlockTree blockTree, IStateReader stateReader, ICodeInfoRepository codeInfoRepository)
            : this(new ChainHeadSpecProvider(specProvider, blockTree), blockTree, new ChainHeadReadOnlyStateProvider(blockTree, stateReader), codeInfoRepository)
        {
        }

        public ChainHeadInfoProvider(ISpecProvider specProvider, IBlockTree blockTree, IReadOnlyStateProvider stateProvider, ICodeInfoRepository codeInfoRepository)
            : this(new ChainHeadSpecProvider(specProvider, blockTree), blockTree, stateProvider, codeInfoRepository)
        {
        }

        [UseConstructorForDependencyInjection]
        public ChainHeadInfoProvider(IChainHeadSpecProvider specProvider, IBlockTree blockTree, IReadOnlyStateProvider stateProvider, ICodeInfoRepository codeInfoRepository)
        {
            SpecProvider = specProvider;
            ReadOnlyStateProvider = stateProvider;
            HeadNumber = blockTree.BestKnownNumber;
            CodeInfoRepository = codeInfoRepository;

            blockTree.BlockAddedToMain += OnHeadChanged;
            _blockTree = blockTree;
        }

        public IChainHeadSpecProvider SpecProvider { get; }

        public IReadOnlyStateProvider ReadOnlyStateProvider { get; }

        public ICodeInfoRepository CodeInfoRepository { get; }

        public long HeadNumber { get; private set; }

        public long? BlockGasLimit { get; internal set; }

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
        public Hash256 StateRoot { get; private set; } = Keccak.EmptyTreeHash;

        public event EventHandler<BlockReplacementEventArgs>? HeadChanged;

        private void OnHeadChanged(object? sender, BlockReplacementEventArgs e)
        {
            IReleaseSpec spec = SpecProvider.GetSpec(e.Block.Header);
            HeadNumber = e.Block.Number;
            BlockGasLimit = e.Block!.GasLimit;
            CurrentBaseFee = e.Block.Header.BaseFeePerGas;
            CurrentFeePerBlobGas =
                BlobGasCalculator.TryCalculateFeePerBlobGas(e.Block.Header, spec.BlobBaseFeeUpdateFraction, out UInt256 currentFeePerBlobGas)
                    ? currentFeePerBlobGas
                    : UInt256.Zero;
            CurrentProofVersion = spec.BlobProofVersion;
            StateRoot = e.Block.StateRoot;
            HeadChanged?.Invoke(sender, e);
        }
    }
}
