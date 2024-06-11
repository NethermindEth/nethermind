// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace Nethermind.Consensus.Processing
{
    public class ReadOnlyTxProcessorSource : IReadOnlyTxProcessorSource
    {
        private readonly ObjectPool<RecyclingTxProcessingScope> _pooledScope;
        private readonly IWorldStateManager _worldStateManager;

        protected readonly ILogManager _logManager;
        protected IBlockTree _blockTree;
        protected ISpecProvider _specProvider;

        public ReadOnlyTxProcessorSource(
            IWorldStateManager worldStateManager,
            IBlockTree blockTree,
            ISpecProvider? specProvider,
            ILogManager? logManager)
            : this(worldStateManager, blockTree.AsReadOnly(), specProvider, logManager)
        {
        }

        public ReadOnlyTxProcessorSource(
            IWorldStateManager worldStateManager,
            IReadOnlyBlockTree readOnlyBlockTree,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            ArgumentNullException.ThrowIfNull(specProvider);
            ArgumentNullException.ThrowIfNull(worldStateManager);

            _worldStateManager = worldStateManager;
            _pooledScope = new DefaultObjectPool<RecyclingTxProcessingScope>(new RecyclingTxProcessingScopePoolPolicy(this));
            _specProvider = specProvider;

            _blockTree = readOnlyBlockTree;
            _logManager = logManager;
        }

        protected virtual ITransactionProcessor CreateTransactionProcessor(IWorldState worldState, IVirtualMachine virtualMachine, ICodeInfoRepository codeInfo)
        {
            return new TransactionProcessor(_specProvider, worldState, virtualMachine, codeInfo, _logManager);
        }

        public virtual IReadOnlyTxProcessingScope Build(IWorldState worldState)
        {
            BlockhashProvider blockhashProvider = new BlockhashProvider(_blockTree, _specProvider, worldState, _logManager);
            CodeInfoRepository codeInfo = new CodeInfoRepository();
            VirtualMachine machine = new VirtualMachine(blockhashProvider, _specProvider, codeInfo, _logManager);
            ITransactionProcessor transactionProcessor = CreateTransactionProcessor(worldState, machine, codeInfo);
            return new ReadOnlyTxProcessingScope(transactionProcessor, worldState);
        }

        public IReadOnlyTxProcessingScope Build(Hash256 stateRoot)
        {
            RecyclingTxProcessingScope scope = _pooledScope.Get();
            scope.WorldState.StateRoot = stateRoot;
            return scope;
        }

        private RecyclingTxProcessingScope NewScope()
        {
            IWorldState newWorldState = _worldStateManager.CreateResettableWorldState();
            Hash256 originalStateRoot = newWorldState.StateRoot;
            IReadOnlyTxProcessingScope baseScope = Build(newWorldState);
            return new RecyclingTxProcessingScope(baseScope, originalStateRoot, this);
        }

        private void Return(RecyclingTxProcessingScope recyclingTxProcessingScope)
        {
            _pooledScope.Return(recyclingTxProcessingScope);
        }

        private class RecyclingTxProcessingScopePoolPolicy(ReadOnlyTxProcessorSource env) : PooledObjectPolicy<RecyclingTxProcessingScope>
        {
            public override RecyclingTxProcessingScope Create()
            {
                return env.NewScope();
            }

            public override bool Return(RecyclingTxProcessingScope obj)
            {
                return true;
            }
        }

        private class RecyclingTxProcessingScope(
            IReadOnlyTxProcessingScope baseScope,
            Hash256 originalStateRoot,
            ReadOnlyTxProcessorSource baseEnv
        ): IReadOnlyTxProcessingScope
        {
            public void Dispose()
            {
                baseScope.WorldState.StateRoot = originalStateRoot;
                baseScope.WorldState.Reset();
                baseEnv.Return(this);
            }

            public ITransactionProcessor TransactionProcessor => baseScope.TransactionProcessor;
            public IWorldState WorldState => baseScope.WorldState;
        }
    }
}
