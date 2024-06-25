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
        public ChainHeadInfoProvider(ISpecProvider specProvider, IBlockTree blockTree, IStateReader stateReader)
            : this(new ChainHeadSpecProvider(specProvider, blockTree), blockTree, new HeadAccountStateProvider(blockTree, stateReader))
        {
        }

        public ChainHeadInfoProvider(ISpecProvider specProvider, IBlockTree blockTree, IAccountStateProvider stateProvider)
            : this(new ChainHeadSpecProvider(specProvider, blockTree), blockTree, stateProvider)
        {
        }

        public ChainHeadInfoProvider(IChainHeadSpecProvider specProvider, IBlockTree blockTree, IAccountStateProvider stateProvider)
        {
            SpecProvider = specProvider;
            AccountStateProvider = stateProvider;
            HeadNumber = blockTree.BestKnownNumber;

            blockTree.BlockAddedToMain += OnHeadChanged;
        }

        public IChainHeadSpecProvider SpecProvider { get; }

        public IAccountStateProvider AccountStateProvider { get; }

        public long HeadNumber { get; private set; }

        public long? BlockGasLimit { get; internal set; }

        public UInt256 CurrentBaseFee { get; private set; }

        public UInt256 CurrentPricePerBlobGas { get; internal set; }

        public event EventHandler<BlockReplacementEventArgs>? HeadChanged;

        private void OnHeadChanged(object? sender, BlockReplacementEventArgs e)
        {
            HeadNumber = e.Block.Number;
            BlockGasLimit = e.Block!.GasLimit;
            CurrentBaseFee = e.Block.Header.BaseFeePerGas;
            CurrentPricePerBlobGas =
                BlobGasCalculator.TryCalculateBlobGasPricePerUnit(e.Block.Header, out UInt256 currentPricePerBlobGas)
                    ? currentPricePerBlobGas
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
        private sealed class HeadAccountStateProvider : IAccountStateProvider
        {
            private readonly IStateReader _stateReader;
            private readonly ReaderWriterLockSlim _lock = new();
            private IScopedStateReader? _reader;
            private Hash256? _stateRoot;

            public HeadAccountStateProvider(IBlockTree blockTree, IStateReader stateReader)
            {
                _stateReader = stateReader;
                blockTree.BlockAddedToMain += OnHeadChanged;
            }

            private void OnHeadChanged(object? sender, BlockReplacementEventArgs e)
            {
                //memorize only state root - do not open reader
                _stateRoot = e.Block.StateRoot;
            }

            private void ResetStateReader(Hash256? stateRoot)
            {
                IScopedStateReader current = !_stateReader.HasStateForRoot(stateRoot) ?
                    _stateReader.ForStateRoot() :
                    _stateReader.ForStateRoot(stateRoot);
                IScopedStateReader? previous;

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

                previous?.Dispose();
            }

            public bool TryGetAccount(Address address, out AccountStruct account)
            {
                _lock.EnterReadLock();
                try
                {
                    //open and dispose new reader for a single get operation - might have a severe performance penalty
                    //changed to ensure no pending readers block page reuse in paprika
                    using IScopedStateReader current = !_stateReader.HasStateForRoot(_stateRoot) ?
                        _stateReader.ForStateRoot() :
                        _stateReader.ForStateRoot(_stateRoot);

                    return current.TryGetAccount(address, out account);
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }
    }
}
