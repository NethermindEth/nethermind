// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Events;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Test;

public partial class BaseEngineModuleTests
{
    public class StoringBlockImprovementContextFactory : IBlockImprovementContextFactory
    {
        private readonly IBlockImprovementContextFactory _blockImprovementContextFactory;
        private readonly bool _skipDuplicatedContext;
        public List<IBlockImprovementContext> CreatedContexts { get; } = new List<IBlockImprovementContext>();

        public event EventHandler<ImprovementStartedEventArgs>? ImprovementStarted;

        public event EventHandler<BlockEventArgs>? BlockImproved;

        public StoringBlockImprovementContextFactory(IBlockImprovementContextFactory blockImprovementContextFactory, bool skipDuplicatedContext = false)
        {
            _blockImprovementContextFactory = blockImprovementContextFactory;
            _skipDuplicatedContext = skipDuplicatedContext;
        }

        public IBlockImprovementContext StartBlockImprovementContext(Block currentBestBlock, BlockHeader parentHeader, PayloadAttributes payloadAttributes, DateTimeOffset startDateTime, UInt256 currentBlockFees, CancellationTokenSource cts)
        {
            IBlockImprovementContext blockImprovementContext = _blockImprovementContextFactory.StartBlockImprovementContext(currentBestBlock, parentHeader, payloadAttributes, startDateTime, currentBlockFees, cts);
            if (_skipDuplicatedContext
                && CreatedContexts.Count > 0
                && CreatedContexts[^1].CurrentBestBlock == blockImprovementContext.CurrentBestBlock)
            {
                return blockImprovementContext;
            }

            lock (CreatedContexts)
            {
                CreatedContexts.Add(blockImprovementContext);
            }
            blockImprovementContext.ImprovementTask.ContinueWith(LogProductionResult);
            Task.Run(() => ImprovementStarted?.Invoke(this, new ImprovementStartedEventArgs(blockImprovementContext)));
            return blockImprovementContext;
        }

        private Block? LogProductionResult(Task<Block?> t)
        {
            if (t.IsCompletedSuccessfully)
            {
                BlockImproved?.Invoke(this, new BlockEventArgs(t.Result!));
            }

            return t.Result;
        }

        public Task WaitForImprovedBlockWithCondition(CancellationToken cancellationToken, Func<Block, bool> cond)
        {
            return Wait.ForEventCondition<BlockEventArgs>(cancellationToken,
                e => BlockImproved += e,
                e => BlockImproved -= e,
                b => cond(b.Block));
        }
    }

    public class ImprovementStartedEventArgs : EventArgs
    {
        public IBlockImprovementContext BlockImprovementContext { get; }

        public ImprovementStartedEventArgs(IBlockImprovementContext blockImprovementContext)
        {
            BlockImprovementContext = blockImprovementContext;
        }
    }
}
