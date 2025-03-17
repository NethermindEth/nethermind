// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Taiko.BlockTransactionExecutors;

public class TaikoRpcBlockTransactionExecutor(
    ITransactionProcessor transactionProcessor,
    IWorldState stateProvider)
    : TaikoBlockValidationTransactionExecutor(new TraceTransactionProcessorAdapter(transactionProcessor),
        stateProvider);
