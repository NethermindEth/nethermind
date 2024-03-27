// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Consensus;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.JsonRpc;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Execution
{
    public abstract class TxBundleExecutor<TResult, TBlockTracer> where TBlockTracer : IBlockTracer
    {
        private readonly ITracerFactory _tracerFactory;
        private readonly ISpecProvider _specProvider;
        private readonly ISigner? _signer;

        protected TxBundleExecutor(ITracerFactory tracerFactory, ISpecProvider specProvider, ISigner? signer)
        {
            _tracerFactory = tracerFactory;
            _specProvider = specProvider;
            _signer = signer;
        }

        public TResult ExecuteBundle(MevBundle bundle, BlockHeader parent, CancellationToken cancellationToken, ulong? timestamp = null)
        {
            Block block = BuildBlock(bundle, parent, timestamp);
            TBlockTracer blockTracer = CreateBlockTracer(bundle);
            ITracer tracer = _tracerFactory.Create();
            tracer.Trace(block, blockTracer.WithCancellation(cancellationToken));
            return BuildResult(bundle, blockTracer);
        }

        protected abstract TResult BuildResult(MevBundle bundle, TBlockTracer tracer);

        private Block BuildBlock(MevBundle bundle, BlockHeader parent, ulong? timestamp)
        {
            BlockHeader header = new(
                parent.Hash ?? Keccak.OfAnEmptySequenceRlp,
                Keccak.OfAnEmptySequenceRlp,
                Beneficiary,
                parent.Difficulty,
                bundle.BlockNumber,
                GetGasLimit(parent),
                timestamp ?? parent.Timestamp,
                Bytes.Empty)
            {
                TotalDifficulty = parent.TotalDifficulty + parent.Difficulty
            };

            header.BaseFeePerGas = BaseFeeCalculator.Calculate(parent, _specProvider.GetSpec(header));
            header.Hash = header.CalculateHash();

            return new Block(header, bundle.Transactions, Array.Empty<BlockHeader>());
        }

        protected virtual long GetGasLimit(BlockHeader parent) => parent.GasLimit;

        protected Address Beneficiary => _signer?.Address ?? Address.Zero;

        protected abstract TBlockTracer CreateBlockTracer(MevBundle mevBundle);

        protected ResultWrapper<TResult> GetInputError(CallOutput result) =>
            ResultWrapper<TResult>.Fail(result.Error ?? string.Empty, ErrorCodes.InvalidInput);


    }
}
