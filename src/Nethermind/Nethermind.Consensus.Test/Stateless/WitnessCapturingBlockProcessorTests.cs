// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class WitnessCapturingBlockProcessorTests
{
    /// <summary>A witnessed block runs the processors below the selector as any block does, once, on the witness graph.</summary>
    [Test]
    public async Task Witnessed_block_runs_the_processors_below_the_selector_once()
    {
        List<(Hash256 Hash, bool OnWitnessGraph)> processed = [];
        List<Hash256> witnessed = [];
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<WitnessRendezvous>()
            .AddSingleton<WitnessCapturingBlockProcessingEnv>()
            .AddSingleton<IMainProcessingModule>(new RecordingModule(processed))
            .AddSingleton<IMainProcessingModule>(new WitnessCapturingMainProcessingModule(true))
            .AddSingleton<IMainProcessingModule>(new RequestingModule(witnessed, requestedBlockNumber: 1)));

        Block first = await chain.AddBlock();
        Block second = await chain.AddBlock();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(witnessed, Is.EqualTo(new[] { first.Hash }));
            Assert.That(processed, Is.EqualTo(new[] { (first.Hash!, true), (second.Hash!, false) }));
        }
    }

    /// <summary>Registered before the selector, so it sits below it and runs on whichever processor the selector picks.</summary>
    private sealed class RecordingModule(List<(Hash256 Hash, bool OnWitnessGraph)> processed) : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder) =>
            builder.AddDecorator<IBlockProcessor>((ctx, inner) => new RecordingProcessor(inner, ctx.Resolve<IWorldState>(), processed));
    }

    private sealed class RecordingProcessor(IBlockProcessor inner, IWorldState state, List<(Hash256 Hash, bool OnWitnessGraph)> processed) : IBlockProcessor
    {
        public event Action<Block>? TransactionsExecuted
        {
            add => inner.TransactionsExecuted += value;
            remove => inner.TransactionsExecuted -= value;
        }

        public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token = default)
        {
            if (suggestedBlock.Number > 0) processed.Add((suggestedBlock.Hash!, state is WitnessGeneratingWorldState));
            return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
        }
    }

    /// <summary>Registered after the selector, so it sits above it and requests the witness the selector then captures.</summary>
    private sealed class RequestingModule(List<Hash256> witnessed, ulong requestedBlockNumber) : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder) =>
            builder.AddDecorator<IBlockProcessor>((ctx, inner) => new RequestingProcessor(inner, ctx.Resolve<WitnessRendezvous>(), witnessed, requestedBlockNumber));
    }

    private sealed class RequestingProcessor(IBlockProcessor inner, WitnessRendezvous rendezvous, List<Hash256> witnessed, ulong requestedBlockNumber) : IBlockProcessor
    {
        public event Action<Block>? TransactionsExecuted
        {
            add => inner.TransactionsExecuted += value;
            remove => inner.TransactionsExecuted -= value;
        }

        public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token = default)
        {
            if (suggestedBlock.Number != requestedBlockNumber) return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);

            using WitnessRequest request = rendezvous.RequestWitness(suggestedBlock.Hash!);
            (Block Block, TxReceipt[] Receipts) result = inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
            if (request.Task.IsCompletedSuccessfully && request.Task.Result is { } witness)
            {
                witness.Dispose();
                witnessed.Add(suggestedBlock.Hash!);
            }

            return result;
        }
    }
}
