// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

public interface IReadOnlyTxProcessingEnv : IReadOnlyTxProcessorSource, IReadOnlyTxProcessingEnvBase
{
    ITransactionProcessor TransactionProcessor { get; set; }
}
