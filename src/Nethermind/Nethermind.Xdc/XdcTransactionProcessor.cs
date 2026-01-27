// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using System.Linq;

namespace Nethermind.Xdc;

internal class XdcTransactionProcessor(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager) : TransactionProcessorBase<EthereumGasPolicy>(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
{
    protected override TransactionResult BuyGas(Transaction tx, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment,
        out UInt256 blobBaseFee)
    {
        if (tx.RequiresSpecialHandling((XdcReleaseSpec)spec) || tx.IsSpecialTransaction((XdcReleaseSpec)spec))
        {
            premiumPerGas = 0;
            senderReservedGasPayment = 0;
            blobBaseFee = 0;
            return TransactionResult.Ok;
        }
        return base.BuyGas(tx, spec, tracer, opts, effectiveGasPrice, out premiumPerGas, out senderReservedGasPayment, out blobBaseFee);
    }

    protected override TransactionResult ValidateSender(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
    {
        var xdcSpec = spec as XdcReleaseSpec;
        Address target = tx.To;
        Address sender = tx.SenderAddress;

        if (xdcSpec.BlackListHFNumber <= header.Number)
        {
            if (IsBlackListed(xdcSpec, sender) || IsBlackListed(xdcSpec, target))
            {
                // Skip processing special transactions if either sender or recipient is blacklisted
                return XdcTransactionResult.ContainsBlacklistedAddress;
            }
        }

        return base.ValidateSender(tx, header, spec, tracer, opts);
    }

    private bool IsBlackListed(IXdcReleaseSpec spec, Address sender)
    {
        return spec.BlackListedAddresses.Contains(sender);
    }

    protected override TransactionResult Execute(Transaction tx, ITxTracer tracer, ExecutionOptions opts)
    {
        BlockHeader header = VirtualMachine.BlockExecutionContext.Header;
        IXdcReleaseSpec spec = GetSpec(header) as IXdcReleaseSpec;

        if (tx.RequiresSpecialHandling(spec))
        {
            return ExecuteSpecialTransaction(tx, tracer, opts);

        }
        return base.Execute(tx, tracer, opts);
    }

    protected override TransactionResult IncrementNonce(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
    {
        var xdcSpec = (IXdcReleaseSpec)spec;
        if (tx.RequiresSpecialHandling(xdcSpec))
        {
            if (tx.IsSignTransaction(xdcSpec))
            {
                var nonce = WorldState.GetNonce(tx.SenderAddress);

                if (nonce < tx.Nonce)
                {
                    return XdcTransactionResult.NonceTooHigh;
                }
                else if (nonce > tx.Nonce)
                {
                    return XdcTransactionResult.NonceTooLow;
                }

                WorldState.IncrementNonce(tx.SenderAddress);
            }

            return TransactionResult.Ok;
        }

        return base.IncrementNonce(tx, header, spec, tracer, opts);
    }

    protected override TransactionResult ValidateGas(Transaction tx, BlockHeader header, long minGasRequired)
    {
        var spec = SpecProvider.GetXdcSpec((XdcBlockHeader)header);
        if (tx.RequiresSpecialHandling(spec))
        {
            return TransactionResult.Ok;
        }
        return base.ValidateGas(tx, header, minGasRequired);
    }

    private TransactionResult ExecuteSpecialTransaction(Transaction tx, ITxTracer tracer, ExecutionOptions opts)
    {
        BlockHeader header = VirtualMachine.BlockExecutionContext.Header;
        IXdcReleaseSpec spec = GetSpec(header) as IXdcReleaseSpec;

        // maybe a better approach would be adding an XdcGasPolicy 
        TransactionResult result;
        IntrinsicGas<EthereumGasPolicy> intrinsicGas = CalculateIntrinsicGas(tx, spec);
        UInt256 effectiveGasPrice = CalculateEffectiveGasPrice(tx, spec.IsEip1559Enabled, header.BaseFeePerGas, out UInt256 opcodeGasPrice);
        bool _ = RecoverSenderIfNeeded(tx, spec, opts, effectiveGasPrice);

        if (!(result = ValidateSender(tx, header, spec, tracer, opts))
            || !(result = IncrementNonce(tx, header, spec, tracer, opts))
            || !(result = ValidateStatic(tx, header, spec, opts, in intrinsicGas)))
        {
            return result;
        }

        // SignTx special stuff has already been handled above
        return ProcessEmptyTransaction(tx, tracer, spec);
    }

    private TransactionResult ProcessEmptyTransaction(Transaction tx, ITxTracer tracer, IReleaseSpec spec)
    {
        WorldState.Commit(spec, tracer.IsTracingState ? tracer : NullStateTracer.Instance, commitRoots: !spec.IsEip658Enabled);

        if (tracer.IsTracingReceipt)
        {
            Hash256 stateRoot = null;
            if (!spec.IsEip658Enabled)
            {
                WorldState.RecalculateStateRoot();
                stateRoot = WorldState.StateRoot;
            }

            var log = new LogEntry(tx.To, [], []);
            tracer.MarkAsSuccess(tx.To, 0, [], [log], stateRoot);
        }

        return TransactionResult.Ok;
    }
}
