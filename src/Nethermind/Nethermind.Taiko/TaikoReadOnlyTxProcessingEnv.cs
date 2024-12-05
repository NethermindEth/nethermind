// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Taiko;

public class TaikoReadOnlyTxProcessingEnv(
  OverridableWorldStateManager worldStateManager,
  IReadOnlyBlockTree readOnlyBlockTree,
  ISpecProvider specProvider,
  ILogManager logManager,
  IWorldState? worldStateToWarmUp = null) : OverridableTxProcessingEnv(
  worldStateManager,
  readOnlyBlockTree,
  specProvider,
  logManager,
  worldStateToWarmUp
 )
{
    protected override ITransactionProcessor CreateTransactionProcessor() =>
        new TaikoTransactionProcessor(SpecProvider, StateProvider, Machine, CodeInfoRepository, LogManager);
}
