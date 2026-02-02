// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Xdc.Contracts;

namespace Nethermind.Xdc;

public sealed class XdcTransactionProcessor : EthereumTransactionProcessorBase
{
    private readonly IMasternodeVotingContract _masternodeVotingContract;

    public XdcTransactionProcessor(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider specProvider,
        IWorldState worldState,
        IVirtualMachine virtualMachine,
        ICodeInfoRepository codeInfoRepository,
        ILogManager logManager,
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
        if (IsSpecialXdcTransaction(tx, header))
            return;

        Address coinbase = header.GasBeneficiary!;
        Address owner = _masternodeVotingContract.GetCandidateOwner(header, coinbase);

        if (owner is null || owner == Address.Zero)
            return;

        UInt256 fee = premiumPerGas * (ulong)spentGas;
        WorldState.AddToBalanceAndCreateIfNotExists(owner, fee, spec);

        if (tracer.IsTracingFees)
        {
            tracer.ReportFees(fee, UInt256.Zero);
        }
    }
}
