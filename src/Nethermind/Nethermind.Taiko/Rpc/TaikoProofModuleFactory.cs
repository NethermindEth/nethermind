// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Taiko.BlockTransactionExecutors;

namespace Nethermind.Taiko.Rpc;

class TaikoProofModuleFactory(
    IWorldStateManager worldStateManager,
    IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory,
    IBlockTree blockTree,
    IBlockPreprocessorStep recoveryStep,
    IReceiptFinder receiptFinder,
    ISpecProvider specProvider,
    ILogManager logManager)
    : ProofModuleFactory(worldStateManager, readOnlyTxProcessingEnvFactory, blockTree, recoveryStep, receiptFinder, specProvider, logManager)
{
    protected override IBlockProcessor.IBlockTransactionsExecutor CreateRpcBlockTransactionsExecutor(IReadOnlyTxProcessingScope scope)
        => new TaikoRpcBlockTransactionExecutor(scope.TransactionProcessor, scope.WorldState);
}
