// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Find;
using Nethermind.Facade.Simulate;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.Trie;
using Block = Nethermind.Core.Block;

namespace Nethermind.Facade
{
    public interface IBlockchainBridge : ILogFinder
    {
        Block HeadBlock { get; }
        bool IsMining { get; }
        void RecoverTxSenders(Block block);
        Address? RecoverTxSender(Transaction tx);
        TxReceipt GetReceipt(Hash256 txHash);
        (TxReceipt? Receipt, TxGasInfo? GasInfo, int LogIndexStart) GetReceiptAndGasInfo(Hash256 txHash);
        (TxReceipt? Receipt, Transaction? Transaction, UInt256? baseFee) GetTransaction(Hash256 txHash, bool checkTxnPool = true);
        CallOutput Call(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride = null, CancellationToken cancellationToken = default);
        SimulateOutput<TTrace> Simulate<TTrace>(BlockHeader header, SimulatePayload<TransactionWithSourceDetails> payload, ISimulateBlockTracerFactory<TTrace> simulateBlockTracerFactory, CancellationToken cancellationToken);
        CallOutput EstimateGas(BlockHeader header, Transaction tx, int errorMarginBasisPoints, Dictionary<Address, AccountOverride>? stateOverride = null, CancellationToken cancellationToken = default);

        CallOutput CreateAccessList(BlockHeader header, Transaction tx, CancellationToken cancellationToken, bool optimize);
        ulong GetChainId();

        int NewBlockFilter();
        int NewPendingTransactionFilter();
        int NewFilter(BlockParameter? fromBlock, BlockParameter? toBlock, object? address = null, IEnumerable<object>? topics = null);
        void UninstallFilter(int filterId);
        bool FilterExists(int filterId);
        Hash256[] GetBlockFilterChanges(int filterId);
        Hash256[] GetPendingTransactionFilterChanges(int filterId);
        FilterLog[] GetLogFilterChanges(int filterId);
        FilterType GetFilterType(int filterId);
        LogFilter GetFilter(BlockParameter fromBlock, BlockParameter toBlock, object? address = null, IEnumerable<object>? topics = null);
        IEnumerable<FilterLog> GetLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default);
        IEnumerable<FilterLog> GetLogs(BlockParameter fromBlock, BlockParameter toBlock, object? address = null, IEnumerable<object>? topics = null, CancellationToken cancellationToken = default);

        bool TryGetLogs(int filterId, out IEnumerable<FilterLog> filterLogs, CancellationToken cancellationToken = default);
        void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, Hash256 stateRoot) where TCtx : struct, INodeContext<TCtx>;
        bool HasStateForRoot(Hash256 stateRoot);
    }
}
