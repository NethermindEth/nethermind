// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            var head = _blockchainBridge.HeadBlock;
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
            Block head = _blockchainBridge.HeadBlock;
            return head?.Hash is null
                ? Task.FromResult<Block?>(null)
                : Task.FromResult(_blockTree.FindBlock(head.Hash));
        }

        public Task<UInt256> GetNonceAsync(Address address)
        {
            return Task.FromResult(_stateReader.GetNonce(_blockchainBridge.HeadBlock.StateRoot, address));
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
            var callOutput = _blockchainBridge.Call(_blockchainBridge.HeadBlock?.Header, transaction, CancellationToken.None);
            return Task.FromResult(callOutput.OutputData ?? new byte[] { 0 });
        }

        public Task<byte[]> CallAsync(Transaction transaction, long blockNumber)
        {
            var block = _blockTree.FindBlock(blockNumber);
            if (block is null)
            {
                return Task.FromResult(Array.Empty<byte>());
            }

            var callOutput = _blockchainBridge.Call(block.Header, transaction, CancellationToken.None);

            return Task.FromResult(callOutput.OutputData ?? new byte[] { 0 });
        }

        public ValueTask<Keccak?> SendOwnTransactionAsync(Transaction transaction)
            => new ValueTask<Keccak?>(_txSender.SendTransaction(transaction, TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast).Result.Hash);
    }
}
