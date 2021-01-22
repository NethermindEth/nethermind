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

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Serialization.Rlp;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    public interface IDebugBridge
    {
        GethLikeTxTrace GetTransactionTrace(Keccak transactionHash, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null);
        GethLikeTxTrace GetTransactionTrace(long blockNumber, int index, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null);
        GethLikeTxTrace GetTransactionTrace(Keccak blockHash, int index, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null);
        GethLikeTxTrace GetTransactionTrace(Rlp blockRlp, Keccak transactionHash, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null);
        GethLikeTxTrace[] GetBlockTrace(Keccak blockHash, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null);
        GethLikeTxTrace[] GetBlockTrace(long blockNumber, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null);
        GethLikeTxTrace[] GetBlockTrace(Rlp blockRlp, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null);
        byte[] GetBlockRlp(Keccak blockHash);
        byte[] GetBlockRlp(long number);
        byte[] GetDbValue(string dbName, byte[] key);
        object GetConfigValue(string category, string name);
        public ChainLevelInfo GetLevelInfo(long number);
        public int DeleteChainSlice(long startNumber);
        public void UpdateHeadBlock(Keccak blockHash);
        Task<bool> MigrateReceipts(long blockNumber);
        void InsertReceipts(BlockParameter blockParameter, TxReceipt[] receipts);
    }
}
