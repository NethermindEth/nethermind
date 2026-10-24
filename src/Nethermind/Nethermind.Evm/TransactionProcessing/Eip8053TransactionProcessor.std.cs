// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>
/// Processes a block's transactions with <paramref name="milliGas"/> when its spec enables EIP-8053, and with
/// <paramref name="legacy"/> otherwise.
/// </summary>
/// <param name="legacy">A processor on <see cref="EthereumGasPolicy"/>.</param>
/// <param name="milliGas">A processor on <see cref="MilliGasPolicy{TPrices}"/>.</param>
/// <remarks>
/// The policy is a type parameter of the EVM, so a fork switches processors instead of a mode, and a fork without
/// EIP-8053 runs the unchanged <see cref="EthereumGasPolicy"/> path. Prototype: no module registers it.
/// </remarks>
public sealed class Eip8053TransactionProcessor(ISpecProvider specProvider, ITransactionProcessor legacy, ITransactionProcessor milliGas) : ITransactionProcessor
{
    private ITransactionProcessor? _current;

    public TransactionResult Process(Transaction transaction, ITxTracer txTracer, ExecutionOptions options) =>
        (_current ?? legacy).Process(transaction, txTracer, options);

    public void SetBlockExecutionContext(BlockHeader blockHeader)
    {
        BlockExecutionContext blockExecutionContext = new(blockHeader, specProvider.GetSpec(blockHeader));
        SetBlockExecutionContext(in blockExecutionContext);
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _current = blockExecutionContext.Spec.IsEip8053Enabled ? milliGas : legacy;
        _current.SetBlockExecutionContext(in blockExecutionContext);
    }
}
