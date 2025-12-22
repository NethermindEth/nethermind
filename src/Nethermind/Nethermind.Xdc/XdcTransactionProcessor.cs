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
using Org.BouncyCastle.Asn1.X509;
using RocksDbSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Nethermind.Xdc;

internal class XdcTransactionProcessor(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager) : TransactionProcessorBase<EthereumGasPolicy>(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
{

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

    protected override TransactionResult ValidateStatic(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts, in IntrinsicGas<EthereumGasPolicy> intrinsicGas)
    {
        var xdcSpec = spec as XdcReleaseSpec;
        Address target = tx.To;

        if (tx.IsSignTransaction(xdcSpec))
        {
            if (tx.Data.Length < 68)
            {
                return TransactionResult.MalformedTransaction;
            }

            UInt256 blkNumber = new UInt256(tx.Data.Span.Slice(4, 32), true);
            if (blkNumber >= header.Number || blkNumber <= (header.Number - (xdcSpec.EpochLength * 2)))
            {
                // Invalid block number in special transaction data
                return TransactionResult.MalformedTransaction;
            }

            return TransactionResult.Ok;
        }

        return base.ValidateStatic(tx, header, spec, opts, intrinsicGas);
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

    private TransactionResult ValidateNonce(Transaction tx, bool skipNonceValidation)
    {
        if(skipNonceValidation)
        {
            return TransactionResult.Ok;
        }

        var nonce = WorldState.GetNonce(tx.SenderAddress);

        if (nonce < tx.Nonce)
        {
            return XdcTransactionResult.NonceTooHigh;
        }
        else if (nonce > tx.Nonce)
        {
            return XdcTransactionResult.NonceTooLow;
        }

        return TransactionResult.Ok;
    }

    private TransactionResult ExecuteSpecialTransaction(Transaction tx, ITxTracer tracer, ExecutionOptions opts)
    {
        BlockHeader header = VirtualMachine.BlockExecutionContext.Header;
        IXdcReleaseSpec spec = GetSpec(header) as IXdcReleaseSpec;

        TransactionResult result;
        IntrinsicGas<EthereumGasPolicy> intrinsicGas = CalculateIntrinsicGas(tx, spec);
        UInt256 effectiveGasPrice = CalculateEffectiveGasPrice(tx, spec.IsEip1559Enabled, header.BaseFeePerGas, out UInt256 opcodeGasPrice);
        bool _ = RecoverSenderIfNeeded(tx, spec, opts, effectiveGasPrice);

        if (!(result = ValidateSender(tx, header, spec, tracer, opts))
            || !(result = ValidateNonce(tx, tx.IsSkipNonceTransaction(spec)))
            || !(result = ValidateStatic(tx, header, spec, opts, in intrinsicGas)))
        {
            return result;
        }

        if (tx.IsSignTransaction(spec))
        {
            return ProcessSignTranscation(tx, tracer, spec, opts);
        }

        if (tx.IsTradingStateTransaction(spec))
        {
            return ProcessEmptyTransaction(tx, tracer, spec);
        }

        if (tx.IsLendingTransaction(spec))
        {
            return ProcessEmptyTransaction(tx, tracer, spec);
        }

        if (tx.IsTradingTransaction(spec))
        {
            return ProcessEmptyTransaction(tx, tracer, spec);
        }

        if (tx.IsLendingFinalizedTradeTransaction(spec))
        {
            return ProcessEmptyTransaction(tx, tracer, spec);
        }

        throw new UnreachableException();
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

    private TransactionResult ProcessSignTranscation(Transaction tx, ITxTracer tracer, IReleaseSpec spec, ExecutionOptions opts)
    {
        WorldState.Commit(spec, tracer.IsTracingState ? tracer : NullStateTracer.Instance, commitRoots: !spec.IsEip658Enabled);

        

        WorldState.IncrementNonce(tx.SenderAddress);

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
