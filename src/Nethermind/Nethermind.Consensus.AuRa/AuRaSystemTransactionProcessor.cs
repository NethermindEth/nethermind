// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// AuRa-flavoured system transaction processor.
/// </summary>
/// <remarks>
/// Differs from the standard <see cref="SystemTransactionProcessor{TGasPolicy}"/> in three places:
/// <list type="bullet">
///   <item>System-user reads are surfaced to the BAL (Parity parity for the AuRa system contracts).</item>
///   <item>The SYSTEM_ADDRESS account is materialised on every non-genesis system call.</item>
///   <item>EIP-158 is disabled for system-transaction state commits even at genesis.</item>
/// </list>
/// </remarks>
public sealed class AuRaSystemTransactionProcessor<TGasPolicy>(
    ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
    ISpecProvider? specProvider,
    IWorldState? worldState,
    IVirtualMachine<TGasPolicy>? virtualMachine,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager? logManager)
    : SystemTransactionProcessor<TGasPolicy>(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    protected override bool ShouldSuppressSystemAccountReads(Transaction tx) => false;

    protected override void OnBeforeSystemTransaction()
    {
        if (!VirtualMachine.BlockExecutionContext.IsGenesis)
        {
            WorldState.CreateAccountIfNotExists(Address.SystemUser, UInt256.Zero, UInt256.Zero);
        }
    }

    protected override IReleaseSpec GetSpec(BlockHeader header) =>
        SpecProvider.GetSpec(header).ForSystemTransaction(isGenesis: false);
}
