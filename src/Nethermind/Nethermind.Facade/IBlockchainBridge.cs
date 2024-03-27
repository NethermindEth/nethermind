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
        (TxReceipt? Receipt, Transaction Transaction, UInt256? baseFee) GetTransaction(Hash256 txHash, bool checkTxnPool = true);
        BlockchainBridge.CallOutput Call(BlockHeader header, Transaction tx, CancellationToken cancellationToken);
        BlockchainBridge.CallOutput EstimateGas(BlockHeader header, Transaction tx, CancellationToken cancellationToken);
        BlockchainBridge.CallOutput CreateAccessList(BlockHeader header, Transaction tx, CancellationToken cancellationToken, bool optimize);
        ulong GetChainId();

        int NewBlockFilter();
        int NewPendingTransactionFilter();
        int NewFilter(BlockParameter? fromBlock, BlockParameter? toBlock, object? address = null, IEnumerable<object>? topics = null);
        void UninstallFilter(int filterId);
        bool FilterExists(int filterId);
        Hash256[] GetBlockFilterChanges(int filterId);
        Hash256[] GetPendingTransactionFilterChanges(int filterId);
        IFilterLog[] GetLogFilterChanges(int filterId);

        FilterType GetFilterType(int filterId);
        IFilterLog[] GetFilterLogs(int filterId);

        LogFilter GetFilter(BlockParameter fromBlock, BlockParameter toBlock, object? address = null, IEnumerable<object>? topics = null);
        IEnumerable<IFilterLog> GetLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default);
        IEnumerable<IFilterLog> GetLogs(BlockParameter fromBlock, BlockParameter toBlock, object? address = null, IEnumerable<object>? topics = null, CancellationToken cancellationToken = default);

        bool TryGetLogs(int filterId, out IEnumerable<IFilterLog> filterLogs, CancellationToken cancellationToken = default);
        void RunTreeVisitor(ITreeVisitor treeVisitor, Hash256 stateRoot);
        bool HasStateForRoot(Hash256 stateRoot);
    }
}
