// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Consensus.Tracing
{
    public class Tracer(
        IWorldState worldState,
        IBlockchainProcessor traceProcessor,
        IBlockchainProcessor executeProcessor,
        ProcessingOptions executeOptions = ProcessingOptions.Trace,
        ProcessingOptions traceOptions = ProcessingOptions.Trace)
        : ITracer
    {
        private void Process(Block block, IBlockTracer blockTracer, IBlockchainProcessor processor, ProcessingOptions options)
        {
            /* We force process since we want to process a block that has already been processed in the past and normally it would be ignored.
               We also want to make it read only so the state is not modified persistently in any way. */

            blockTracer.StartNewBlockTrace(block);
            processor.Process(block, options, blockTracer);
            blockTracer.EndBlockTrace();
        }

        public void Trace(Block block, IBlockTracer tracer) => Process(block, tracer, traceProcessor, traceOptions);

        public void Execute(Block block, IBlockTracer tracer) => Process(block, tracer, executeProcessor, executeOptions);

        public void Accept<TCtx>(ITreeVisitor<TCtx> visitor, Hash256 stateRoot) where TCtx : struct, INodeContext<TCtx>
        {
            ArgumentNullException.ThrowIfNull(visitor);
            ArgumentNullException.ThrowIfNull(stateRoot);

            worldState.Accept(visitor, stateRoot);
        }
    }

    public interface ITracerEnv
    {
        ITracerScope RunInProcessingScope(BlockHeader block);
        ITracerScope RunInProcessingScope(BlockHeader block, Dictionary<Address, AccountOverride>? stateOverride);
    }

    public interface ITracerScope : IDisposable
    {
        ITracer Tracer { get; }
    }

    /// <summary>
    /// Wrapper around an <see cref="ITracer"> and <see cref="IOverridableTxProcessorSource"/> that owns the states
    /// that the tracer is using. Used to hide the state neatly.
    /// </summary>
    /// <param name="theTracer"></param>
    /// <param name="envSource"></param>
    public class TracerEnv(ITracer theTracer, IOverridableTxProcessorSource envSource) : ITracerEnv
    {
        public ITracerScope RunInProcessingScope(BlockHeader block) => new TracerScope(theTracer, envSource.Build(block.StateRoot));

        public ITracerScope RunInProcessingScope(BlockHeader header, Dictionary<Address, AccountOverride>? stateOverride) => new TracerScope(theTracer, envSource.BuildAndOverride(header, stateOverride));

        private class TracerScope(ITracer theTracer, IOverridableTxProcessingScope scope) : ITracerScope
        {
            public ITracer Tracer => theTracer;
            public void Dispose()
            {
                scope.Dispose();
            }
        }
    }
}
