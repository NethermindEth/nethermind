//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;
using Block = Nethermind.Core.Block;

namespace Nethermind.Facade
{
    public interface IBlockchainBridge : ILogFinder
    {
        Block BeamHead { get; }
        bool IsMining { get; }
        void RecoverTxSenders(Block block);
        void RecoverTxSender(Transaction tx);
        TxReceipt GetReceipt(Keccak txHash);
        (TxReceipt Receipt, UInt256? EffectiveGasPrice) GetReceiptAndEffectiveGasPrice(Keccak txHash);
        (TxReceipt Receipt, Transaction Transaction, UInt256? baseFee) GetTransaction(Keccak txHash);
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
        void RunTreeVisitor(ITreeVisitor treeVisitor, Keccak stateRoot);
        
    }
}
