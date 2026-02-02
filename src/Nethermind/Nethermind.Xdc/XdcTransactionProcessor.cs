// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

internal class XdcTransactionProcessor : EthereumTransactionProcessorBase
{
    private readonly IMasternodeVotingContract _masternodeVotingContract;

    public XdcTransactionProcessor(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager,
        IMasternodeVotingContract masternodeVotingContract)
        : base(
            blobBaseFeeCalculator,
            specProvider,
            worldState,
            virtualMachine,
            codeInfoRepository,
            logManager)
    {
        _masternodeVotingContract = masternodeVotingContract;
    }

    protected override void PayFees(
        Transaction tx,
        BlockHeader header,
        IReleaseSpec spec,
        ITxTracer tracer,
        in TransactionSubstate substate,
        long spentGas,
        in UInt256 premiumPerGas,
        in UInt256 blobBaseFee,
        int statusCode)
    {
        IXdcReleaseSpec xdcSpec = (IXdcReleaseSpec)spec;

        if (tx.IsSpecialTransaction(xdcSpec)) return;

        if (!xdcSpec.IsTipTrc21FeeEnabled)
        {
            base.PayFees(tx, header, spec, tracer, substate, spentGas, premiumPerGas, blobBaseFee, statusCode);
            return;
        }

        Address coinbase = header.GasBeneficiary!;
        Address owner = _masternodeVotingContract.GetCandidateOwner(header, coinbase);

        if (owner is null || owner == Address.Zero)
            return;

        UInt256 fee = premiumPerGas * (ulong)spentGas;
        WorldState.AddToBalanceAndCreateIfNotExists(owner, fee, spec);

        if (tracer.IsTracingFees)
            tracer.ReportFees(fee, UInt256.Zero);
    }

    protected override TransactionResult BuyGas(Transaction tx, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment,
        out UInt256 blobBaseFee)
    {
        if (tx.RequiresSpecialHandling((XdcReleaseSpec)spec))
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

        if (xdcSpec.IsBlackListingEnabled)
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

    protected override UInt256 CalculateEffectiveGasPrice(Transaction tx, bool eip1559Enabled, in UInt256 baseFee, out UInt256 opcodeGasPrice)
    {
        // IMPORTANT: if we override the effective gas price to 0, we must also set opcodeGasPrice to 0.
        // TxExecutionContext is created with opcodeGasPrice and is later used for refunding, tracing, etc.
        //
        // Also: IsSpecialTransaction requires the IXdcReleaseSpec to decide Randomize vs BlockSigner, so
        // we need the current block spec here.
        IXdcReleaseSpec xdcSpec = (IXdcReleaseSpec)VirtualMachine.BlockExecutionContext.Spec;

        if (tx.IsSpecialTransaction(xdcSpec))
        {
            opcodeGasPrice = UInt256.Zero;
            return UInt256.Zero;
        }

        return base.CalculateEffectiveGasPrice(tx, eip1559Enabled, in baseFee, out opcodeGasPrice);
    }

    protected override IntrinsicGas<EthereumGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec)
    {
        if (tx.RequiresSpecialHandling((IXdcReleaseSpec)spec))
        {
            return new IntrinsicGas<EthereumGasPolicy>();
        }

        return base.CalculateIntrinsicGas(tx, spec);
    }

    private TransactionResult ExecuteSpecialTransaction(Transaction tx, ITxTracer tracer, ExecutionOptions opts)
    {
        BlockHeader header = VirtualMachine.BlockExecutionContext.Header;
        IXdcReleaseSpec spec = GetSpec(header) as IXdcReleaseSpec;

        bool restore = opts.HasFlag(ExecutionOptions.Restore);

        // maybe a better approach would be adding an XdcGasPolicy
        TransactionResult result;
        _ = RecoverSenderIfNeeded(tx, spec, opts, UInt256.Zero);
        IntrinsicGas<EthereumGasPolicy> intrinsicGas = CalculateIntrinsicGas(tx, spec);

        if (!(result = ValidateSender(tx, header, spec, tracer, opts))
            || !(result = IncrementNonce(tx, header, spec, tracer, opts))
            || !(result = ValidateStatic(tx, header, spec, opts, intrinsicGas)))
        {
            if (restore)
            {
                WorldState.Reset(resetBlockChanges: false);
            }
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
