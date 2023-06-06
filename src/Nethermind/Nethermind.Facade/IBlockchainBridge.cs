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
        TxReceipt GetReceipt(Keccak txHash);
        (TxReceipt? Receipt, TxGasInfo? GasInfo, int LogIndexStart) GetReceiptAndGasInfo(Keccak txHash);
        (TxReceipt? Receipt, Transaction Transaction, UInt256? baseFee) GetTransaction(Keccak txHash);
        BlockchainBridge.CallOutput Call(BlockHeader header, Transaction tx, CancellationToken cancellationToken);
        BlockchainBridge.CallOutput EstimateGas(BlockHeader header, Transaction tx, CancellationToken cancellationToken);
        BlockchainBridge.CallOutput CreateAccessList(BlockHeader header, Transaction tx, CancellationToken cancellationToken, bool optimize);
        ulong GetChainId();

        int NewBlockFilter();
        int NewPendingTransactionFilter();
        int NewFilter(BlockParameter fromBlock, BlockParameter toBlock, object? address = null, IEnumerable<object>? topics = null);
        void UninstallFilter(int filterId);
        bool FilterExists(int filterId);
        Keccak[] GetBlockFilterChanges(int filterId);
        Keccak[] GetPendingTransactionFilterChanges(int filterId);
        FilterLog[] GetLogFilterChanges(int filterId);

        FilterType GetFilterType(int filterId);
        FilterLog[] GetFilterLogs(int filterId);

        IEnumerable<FilterLog> GetLogs(BlockParameter fromBlock, BlockParameter toBlock, object? address = null, IEnumerable<object>? topics = null, CancellationToken cancellationToken = default);
        bool TryGetLogs(int filterId, out IEnumerable<FilterLog> filterLogs, CancellationToken cancellationToken = default);
        void RunTreeVisitor(ITreeVisitor treeVisitor, Keccak stateRoot);

    }
}
