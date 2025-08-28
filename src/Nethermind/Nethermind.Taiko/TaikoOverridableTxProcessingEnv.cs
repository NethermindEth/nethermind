// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Taiko;

public class TaikoOverridableTxProcessingEnv(
    IOverridableWorldScope worldScope,
    IReadOnlyBlockTree readOnlyBlockTree,
    ISpecProvider specProvider,
    ILogManager logManager,
    IPrecompileChecker precompileChecker) : OverridableTxProcessingEnv(
    worldScope,
    readOnlyBlockTree,
    specProvider,
    logManager,
    precompileChecker
)
{
    protected override ITransactionProcessor CreateTransactionProcessor() =>
        new TaikoTransactionProcessor(SpecProvider, StateProvider, Machine, CodeInfoRepository, LogManager);
}
