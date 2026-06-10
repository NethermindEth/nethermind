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
/// AuRa-flavoured system transaction processor.
/// </summary>
/// <remarks>
/// Differs from the standard <see cref="SystemTransactionProcessor{TGasPolicy}"/> in three places:
/// <list type="bullet">
///   <item>System-user reads are surfaced to the BAL (parity with AuRa system contracts).</item>
///   <item>The SYSTEM_ADDRESS account is materialised on every non-genesis system call so the BAL records the access.</item>
///   <item>EIP-158 stays disabled for system-transaction state commits even at genesis.</item>
/// </list>
/// AuRa semantics are guarded by <c>SpecProvider.SealEngine == AuRa</c>: when the seal engine is not
/// AuRa (e.g. tests with a generic <c>TestSpecProvider</c>), this subclass falls back to the base
/// processor's behaviour so existing test expectations are preserved.
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

/// <summary>
/// Factory binding for <see cref="AuRaSystemTransactionProcessor{TGasPolicy}"/>. Registered by
/// the AuRa plugin so every <see cref="TransactionProcessorBase{TGasPolicy}"/> — including BAL
/// parallel-pool workers that hand-build their own processor — picks up the AuRa subclass.
/// </summary>
public sealed class AuRaSystemTransactionProcessorFactory<TGasPolicy>
    : ISystemTransactionProcessorFactory<TGasPolicy>
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    public SystemTransactionProcessor<TGasPolicy> Create(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider specProvider,
        IWorldState worldState,
        IVirtualMachine<TGasPolicy> virtualMachine,
        ICodeInfoRepository codeInfoRepository,
        ILogManager logManager)
        => new AuRaSystemTransactionProcessor<TGasPolicy>(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager);
}
