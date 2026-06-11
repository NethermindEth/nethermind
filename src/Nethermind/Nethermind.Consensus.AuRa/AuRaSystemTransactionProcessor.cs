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

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// AuRa-flavoured system transaction processor. Surfaces system-user reads to the BAL,
/// materialises SYSTEM_ADDRESS on non-genesis system calls, and keeps EIP-158 disabled for
/// system-tx state commits at genesis. Guarded by <c>SpecProvider.SealEngine == AuRa</c> so it
/// falls back to base behaviour under non-AuRa test specs.
/// </summary>
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
    private readonly bool _isAura = specProvider?.SealEngine == SealEngineType.AuRa;

    protected override bool ShouldSuppressSystemAccountReads(Transaction tx) =>
        !_isAura && base.ShouldSuppressSystemAccountReads(tx);

    protected override void OnBeforeSystemTransaction()
    {
        if (_isAura && !VirtualMachine.BlockExecutionContext.IsGenesis)
        {
            WorldState.CreateAccountIfNotExists(Address.SystemUser, UInt256.Zero, UInt256.Zero);
        }
    }

    protected override bool TreatAsGenesisForSpec(BlockHeader header) =>
        !_isAura && base.TreatAsGenesisForSpec(header);
}
