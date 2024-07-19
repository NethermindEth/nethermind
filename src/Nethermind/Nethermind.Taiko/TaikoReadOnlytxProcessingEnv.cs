// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Taiko;

public class TaikoReadOnlyTxProcessingEnv(
      IWorldStateManager worldStateManager,
      IReadOnlyBlockTree readOnlyBlockTree,
      ISpecProvider specProvider,
      ILogManager logManager,
      IWorldState? worldStateToWarmUp = null) : ReadOnlyTxProcessingEnv(
      worldStateManager,
      readOnlyBlockTree,
      specProvider,
      logManager,
      worldStateToWarmUp
     )
{
    protected override TransactionProcessor CreateTransactionProcessor() =>
        new TaikoTransactionProcessor(SpecProvider, StateProvider, Machine, CodeInfoRepository, _logManager);
}
