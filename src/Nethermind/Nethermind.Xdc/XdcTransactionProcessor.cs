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
    private readonly ITrc21StateReader _trc21StateReader;

    public XdcTransactionProcessor(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager,
        IMasternodeVotingContract masternodeVotingContract,
        ITrc21StateReader trc21StateReader)
        : base(
            blobBaseFeeCalculator,
            specProvider,
            worldState,
            virtualMachine,
            codeInfoRepository,
            logManager)
    {
        _masternodeVotingContract = masternodeVotingContract;
        _trc21StateReader = trc21StateReader;
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

        Address owner = _masternodeVotingContract.GetCandidateOwner(WorldState, header.GasBeneficiary!);

        if (owner is null || owner == Address.Zero)
            return;

        UInt256 effectiveGasPrice = CalculateEffectiveGasPrice(tx, spec.IsEip1559Enabled, header.BaseFeePerGas, out UInt256 opcodeGasPrice);
        UInt256 fee = effectiveGasPrice * (ulong)spentGas;

        WorldState.AddToBalanceAndCreateIfNotExists(owner, fee, spec);

        if (tracer.IsTracingFees)
            tracer.ReportFees(fee, UInt256.Zero);
    }

    protected override TransactionResult BuyGas(Transaction tx, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
        in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment,
        out UInt256 blobBaseFee)
    {
        premiumPerGas = UInt256.Zero;
        senderReservedGasPayment = UInt256.Zero;
        blobBaseFee = UInt256.Zero;

        IXdcReleaseSpec xdcSpec = (IXdcReleaseSpec)spec;
        if (tx.RequiresSpecialHandling(xdcSpec) || tx.IsSpecialTransaction(xdcSpec))
            return TransactionResult.Ok;

        XdcBlockHeader header = (XdcBlockHeader)VirtualMachine.BlockExecutionContext.Header;
        if (!TryGetTrc21FeeBalance(tx, header, xdcSpec, out UInt256 tokenFeeBalance))
            return base.BuyGas(tx, spec, tracer, opts, effectiveGasPrice, out premiumPerGas, out senderReservedGasPayment, out blobBaseFee);

        bool validate = ShouldValidateGas(tx, opts);
        if (validate && !TryCalculatePremiumPerGas(tx, header.BaseFeePerGas, out premiumPerGas))
        {
            TraceLogInvalidTx(tx, "MINER_PREMIUM_IS_NEGATIVE");
            return TransactionResult.MinerPremiumNegative;
        }

        UInt256 senderBalance = WorldState.GetBalance(tx.SenderAddress!);
        if (UInt256.SubtractUnderflow(in senderBalance, in tx.ValueRef, out _))
        {
            TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}");
            return TransactionResult.InsufficientSenderBalance;
        }

        bool overflows = UInt256.MultiplyOverflow((UInt256)tx.GasLimit, effectiveGasPrice, out UInt256 totalGasPayment);

        if (overflows || tokenFeeBalance < totalGasPayment)
        {
            TraceLogInvalidTx(tx, $"INSUFFICIENT_TOKEN_FEE_BALANCE: ({tx.SenderAddress})_BALANCE = {tokenFeeBalance}");
            return TransactionResult.InsufficientSenderBalance;
        }

        return TransactionResult.Ok;
    }

    protected override TransactionResult ValidateSender(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
    {
        IXdcReleaseSpec xdcSpec = (IXdcReleaseSpec)spec;
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
            return ExecuteSpecialTransaction(tx, tracer, opts);

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

    protected override void PayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec)
    {
        BlockHeader header = VirtualMachine.BlockExecutionContext.Header;
        IXdcReleaseSpec xdcSpec = (IXdcReleaseSpec)GetSpec(header);
        if (TryGetTrc21FeeBalance(tx, header, xdcSpec, out _))
        {
            return;
        }

        base.PayRefund(tx, refundAmount, spec);
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

        XdcBlockHeader header = (XdcBlockHeader)VirtualMachine.BlockExecutionContext.Header;
        if (TryGetTrc21FeeBalance(tx, header, xdcSpec, out _))
        {
            opcodeGasPrice = GetTrc21GasPriceForBlock(header.Number, xdcSpec);
            return opcodeGasPrice;
        }

        return base.CalculateEffectiveGasPrice(tx, eip1559Enabled, in baseFee, out opcodeGasPrice);
    }

    protected override IntrinsicGas<EthereumGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec) =>
        tx.RequiresSpecialHandling((IXdcReleaseSpec)spec)
            ? new IntrinsicGas<EthereumGasPolicy>()
            : base.CalculateIntrinsicGas(tx, spec);

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

    private bool TryGetTrc21FeeBalance(Transaction tx, BlockHeader header, IXdcReleaseSpec xdcSpec, out UInt256 tokenFeeBalance)
    {
        tokenFeeBalance = UInt256.Zero;
        if (!xdcSpec.IsTipTrc21FeeEnabled || tx.To is null || tx.SenderAddress is null)
        {
            return false;
        }

        Address token = tx.To;
        XdcBlockHeader xdcHeader = (XdcBlockHeader)header;
        if (!_trc21StateReader.GetFeeCapacities(xdcHeader).TryGetValue(token, out tokenFeeBalance))
        {
            return false;
        }

        return _trc21StateReader.ValidateTransaction(xdcHeader, tx.SenderAddress, token, tx.Data.Span);
    }

    private static UInt256 GetTrc21GasPriceForBlock(long blockNumber, IXdcReleaseSpec spec)
    {
        if (blockNumber >= spec.BlockNumberGas50x)
        {
            return XdcConstants.GasPrice50x;
        }

        if (blockNumber > spec.TipTrc21FeeBlock)
        {
            return XdcConstants.Trc21GasPrice;
        }

        return XdcConstants.Trc21GasPriceBefore;
    }
}
