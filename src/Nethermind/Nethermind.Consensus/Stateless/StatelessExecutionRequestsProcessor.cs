// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Execution requests processor for stateless (zkEVM) re-execution.
/// </summary>
/// <remarks>
/// Runs the request system calls exactly like <see cref="ExecutionRequestsProcessor"/> so their
/// state effects are applied, but does not write the derived requests or requests hash back to the
/// block. The requests hash is a consensus-layer attestation the stateless guest does not re-derive;
/// leaving the consensus-layer-provided value on the header keeps stateless validation aligned with
/// the tests-zkevm fixtures, which treat requests-hash mismatches as statelessly valid.
/// </remarks>
public sealed class StatelessExecutionRequestsProcessor(ITransactionProcessor transactionProcessor)
    : ExecutionRequestsProcessor(transactionProcessor)
{
    protected override void RecordRequests(Block block, ref ArrayPoolListRef<byte[]> requests)
    {
    }
}
