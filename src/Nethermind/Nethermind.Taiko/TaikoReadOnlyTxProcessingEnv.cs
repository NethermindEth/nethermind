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
  IOverridableWorldScope worldStateManager,
  IReadOnlyBlockTree readOnlyBlockTree,
  ISpecProvider specProvider,
  ILogManager logManager) : OverridableTxProcessingEnv(
  worldStateManager,
  readOnlyBlockTree,
  specProvider,
  logManager
)
{
    protected override ITransactionProcessor CreateTransactionProcessor() =>
        new TaikoTransactionProcessor(SpecProvider, StateProvider, Machine, CodeInfoRepository, LogManager);
}
