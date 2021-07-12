//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.DataMarketplace.Core.Services
{
    public class NdmBlockchainBridge : INdmBlockchainBridge
    {
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly ITxSender _txSender;
        private readonly IBlockFinder _blockTree;
        private readonly IStateReader _stateReader;

        public NdmBlockchainBridge(
            IBlockchainBridge blockchainBridge,
            IBlockFinder blockTree,
            IStateReader stateReader,
            ITxSender txSender)
        {
            _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
            _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
        }

        public Task<long> GetLatestBlockNumberAsync()
        {
            var head = _blockchainBridge.BeamHead;
            return head is null ? Task.FromResult(0L) : Task.FromResult(head.Number);
        }

        public Task<byte[]> GetCodeAsync(Address address)
        {
            byte[] code = _stateReader.GetCode(_blockTree.Head?.StateRoot ?? Keccak.EmptyTreeHash, address);
            return Task.FromResult(code);   
        }

        public Task<Block?> FindBlockAsync(Keccak blockHash)
        {
            return Task.FromResult(_blockTree.FindBlock(blockHash));   
        }

        public Task<Block?> FindBlockAsync(long blockNumber) =>
            Task.FromResult(_blockTree.FindBlock(blockNumber));

        public Task<Block?> GetLatestBlockAsync()
        {
            Block head = _blockchainBridge.BeamHead;
            return head?.Hash is null
                ? Task.FromResult<Block?>(null)
                : Task.FromResult(_blockTree.FindBlock(head.Hash));
        }

        public Task<UInt256> GetNonceAsync(Address address)
        {
            return Task.FromResult(_stateReader.GetNonce(_blockchainBridge.BeamHead.StateRoot, address));   
        }

        public Task<NdmTransaction?> GetTransactionAsync(Keccak transactionHash)
        {
            (TxReceipt receipt, Transaction transaction, UInt256? baseFee) = _blockchainBridge.GetTransaction(transactionHash);
            if (transaction is null)
            {
                return Task.FromResult<NdmTransaction?>(null);
            }

            var isPending = receipt is null;

            return Task.FromResult<NdmTransaction?>(new NdmTransaction(transaction, isPending, receipt?.BlockNumber ?? 0,
                receipt?.BlockHash, receipt?.GasUsed ?? 0));
        }

        public Task<ulong> GetNetworkIdAsync() => Task.FromResult(_blockchainBridge.GetChainId());

        public Task<byte[]> CallAsync(Transaction transaction)
        {
            var callOutput = _blockchainBridge.Call(_blockchainBridge.BeamHead?.Header, transaction, CancellationToken.None);
            return Task.FromResult(callOutput.OutputData ?? new byte[] {0});
        }

        public Task<byte[]> CallAsync(Transaction transaction, long blockNumber)
        {
            var block = _blockTree.FindBlock(blockNumber);
            if (block is null)
            {
                return Task.FromResult(Array.Empty<byte>());
            }

            var callOutput = _blockchainBridge.Call(block.Header, transaction, CancellationToken.None);

            return Task.FromResult(callOutput.OutputData ?? new byte[] {0});
        }

        public ValueTask<Keccak?> SendOwnTransactionAsync(Transaction transaction)
            => new ValueTask<Keccak?>(_txSender.SendTransaction(transaction, TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast).Result.Hash);
    }
}
