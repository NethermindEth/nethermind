// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// AuRa's drop-in replacement for <see cref="EthereumTransactionProcessor"/>.
/// </summary>
/// <remarks>
/// Identical to the base except that on-demand system transactions are dispatched through
/// <see cref="AuRaSystemTransactionProcessor{TGasPolicy}"/>, which preserves the AuRa-specific
/// pre-execution state semantics that used to live in <c>SystemTransactionProcessor</c>.
/// </remarks>
public sealed class AuRaEthereumTransactionProcessor(
    ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
    ISpecProvider? specProvider,
    IWorldState? worldState,
    IVirtualMachine? virtualMachine,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager? logManager)
    : EthereumTransactionProcessor(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
{
    protected override SystemTransactionProcessor<EthereumGasPolicy> CreateSystemTransactionProcessor() =>
        new AuRaSystemTransactionProcessor<EthereumGasPolicy>(
            _blobBaseFeeCalculator, SpecProvider, WorldState, VirtualMachine, _codeInfoRepository, _logManager);
}
