/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.Numerics;
using System.Security;
using Nethermind.Blockchain.Filters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Block = Nethermind.Core.Block;
using Transaction = Nethermind.Core.Transaction;
using TransactionReceipt = Nethermind.Core.TransactionReceipt;
using TransactionTrace = Nethermind.Evm.TransactionTrace;

namespace Nethermind.JsonRpc.Module
{
    public interface IBlockchainBridge
    {
        (IReadOnlyCollection<Address> Addresses, Result Result) GetKeyAddresses();
        (PrivateKey PrivateKey, Result Result) GetKey(Address address, SecureString password);

        BlockHeader Head { get; }
        BlockHeader BestSuggested { get; }
        Block FindBlock(Keccak blockHash, bool mainChainOnly);
        Block FindBlock(UInt256 blockNumber);
        Block RetrieveHeadBlock();
        Block RetrieveGenesisBlock();

        Signature Sign(PrivateKey privateKey, Keccak message);

        void AddTxData(UInt256 blockNumber);
        Transaction GetTransaction(Keccak transactionHash);
        Keccak GetBlockHash(Keccak transactionHash);
        void SendTransaction(Transaction transaction);
        TransactionReceipt GetTransactionReceipt(Keccak transactionHash);
        TransactionTrace GetTransactionTrace(Keccak transactionHash);
        TransactionTrace GetTransactionTrace(UInt256 blockNumber, int index);
        TransactionTrace GetTransactionTrace(Keccak blockHash, int index);
        BlockTrace GetBlockTrace(Keccak blockHash);
        BlockTrace GetBlockTrace(UInt256 blockNumber);
        byte[] GetDbValue(string dbName, byte[] key);

        byte[] GetCode(Address address);
        byte[] GetCode(Keccak codeHash);
        BigInteger GetNonce(Address address);
        BigInteger GetBalance(Address address);

        Account GetAccount(Address address, Keccak stateRoot);
        int GetNetworkId();
        int NewBlockFilter();
        object[] GetFilterChanges(int filterId);

        int NewFilter(Block fromBlock, Block toBlock, object address = null,
            IEnumerable<object> topics = null);
    }
}