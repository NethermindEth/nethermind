// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain.Spec;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
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
            : this(new ChainHeadSpecProvider(specProvider, blockTree), blockTree, new HeadAccountStateProvider(blockTree, stateReader), codeInfoRepository)
        {
        }

        public ChainHeadInfoProvider(ISpecProvider specProvider, IBlockTree blockTree, IAccountStateProviderWithCode stateProvider, ICodeInfoRepository codeInfoRepository)
            : this(new ChainHeadSpecProvider(specProvider, blockTree), blockTree, stateProvider, codeInfoRepository)
        {
        }

        public ChainHeadInfoProvider(IChainHeadSpecProvider specProvider, IBlockTree blockTree, IAccountStateProviderWithCode stateProvider, ICodeInfoRepository codeInfoRepository)
        {
            SpecProvider = specProvider;
            ReadOnlyStateProvider = stateProvider;
            HeadNumber = blockTree.BestKnownNumber;
            CodeInfoRepository = codeInfoRepository;

            blockTree.BlockAddedToMain += OnHeadChanged;
            _blockTree = blockTree;
        }

        public IChainHeadSpecProvider SpecProvider { get; }

        public IAccountStateProviderWithCode ReadOnlyStateProvider { get; }

        public ICodeInfoRepository CodeInfoRepository { get; }

        public long HeadNumber { get; private set; }

        public long? BlockGasLimit { get; internal set; }

        public UInt256 CurrentBaseFee { get; private set; }

        public UInt256 CurrentFeePerBlobGas { get; internal set; }

        public bool IsSyncing
        {
            get
            {
                if (HasSynced)
                {
                    return false;
                }

                (bool isSyncing, _, _) = _blockTree.IsSyncing(maxDistanceForSynced: 2);
                return isSyncing;
            }
        }

        public event EventHandler<BlockReplacementEventArgs>? HeadChanged;

        private void OnHeadChanged(object? sender, BlockReplacementEventArgs e)
        {
            HeadNumber = e.Block.Number;
            BlockGasLimit = e.Block!.GasLimit;
            CurrentBaseFee = e.Block.Header.BaseFeePerGas;
            CurrentFeePerBlobGas =
                BlobGasCalculator.TryCalculateFeePerBlobGas(e.Block.Header, out UInt256 currentFeePerBlobGas)
                    ? currentFeePerBlobGas
                    : UInt256.Zero;
            HeadChanged?.Invoke(sender, e);
        }

        /// <summary>
        /// Provides a custom implementation of <see cref="IAccountStateProvider"/> that
        /// is capable of following the head changes and replace the underlying reader.
        /// This makes it responsible for providing the scoping.
        ///
        /// The swap is done using a short-lived RLW.Write lock so that it should not hit readers hard.
        /// </summary>
        private sealed class HeadAccountStateProvider : IAccountStateProviderWithCode
        {
            private readonly IStateReader _stateReader;
            private readonly ReaderWriterLockSlim _lock = new();
            private IScopedStateReader _reader;

            public HeadAccountStateProvider(IBlockTree blockTree, IStateReader stateReader)
            {
                _stateReader = stateReader;
                blockTree.BlockAddedToMain += OnHeadChanged;

                // start with the most recent
                _reader = _stateReader.ForStateRoot();
            }

            private void OnHeadChanged(object? sender, BlockReplacementEventArgs e)
            {
                IScopedStateReader current = _stateReader.ForStateRoot(e.Block.StateRoot);
                IScopedStateReader previous;

                _lock.EnterWriteLock();
                try
                {
                    previous = _reader;
                    _reader = current;
                }
                finally
                {
                    _lock.ExitWriteLock();
                }

                previous.Dispose();
            }

            public bool TryGetAccount(Address address, out AccountStruct account)
            {
                _lock.EnterReadLock();
                try
                {
                    return _reader.TryGetAccount(address, out account);
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }

            public byte[]? GetCode(Address address) => _stateReader.GetCode(_reader.StateRoot, address);

            public byte[]? GetCode(Hash256 codeHash) => _stateReader.GetCode(codeHash);

            public byte[]? GetCode(ValueHash256 codeHash) => _stateReader.GetCode(codeHash);
        }
    }
}
