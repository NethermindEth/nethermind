// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Stats.Model;

namespace Nethermind.Synchronization.Test
{
    public class SyncPeerMock : ISyncPeer
    {
        public string Name => "Mock";
        private readonly IBlockTree _remoteTree;
        private readonly ISyncServer? _remoteSyncServer;
        private readonly TaskCompletionSource _closeTaskCompletionSource = new();

        public SyncPeerMock(IBlockTree remoteTree, PublicKey? localPublicKey = null, string localClientId = "", ISyncServer? remoteSyncServer = null, PublicKey? remotePublicKey = null, string remoteClientId = "")
        {
            string localHost = "127.0.0.1";
            if (int.TryParse(localClientId.Replace("PEER", string.Empty), out int localIndex))
            {
                localHost = $"127.0.0.{localIndex}";
            }

            string remoteHost = "127.0.0.1";
            if (int.TryParse(remoteClientId.Replace("PEER", string.Empty), out int remoteIndex))
            {
                remoteHost = $"127.0.0.{remoteIndex}";
            }

            _remoteTree = remoteTree;
            Block remoteTreeHead = _remoteTree.Head!;
            HeadNumber = remoteTreeHead.Number;
            HeadHash = remoteTreeHead.Hash!;
            TotalDifficulty = remoteTreeHead.TotalDifficulty ?? 0;

            _remoteSyncServer = remoteSyncServer;
            Node = new Node(remotePublicKey ?? TestItem.PublicKeyA, remoteHost, 1234);
            LocalNode = new Node(localPublicKey ?? TestItem.PublicKeyB, localHost, 1235);
            Node.ClientId = remoteClientId;
            LocalNode.ClientId = localClientId;

            Task.Factory.StartNew(RunQueue, TaskCreationOptions.LongRunning);
        }

        private void RunQueue()
        {
            foreach (Action action in _sendQueue.GetConsumingEnumerable())
            {
                action();
            }

            _closeTaskCompletionSource.SetResult();
        }

        public Node Node { get; }

        private Node LocalNode { get; }
        public byte ProtocolVersion { get; } = default;
        public string ProtocolCode { get; } = null!;
        public string ClientId => Node.ClientId;
        public Hash256 HeadHash { get; set; }
        public long HeadNumber { get; set; }
        public UInt256 TotalDifficulty { get; set; }
        public bool IsInitialized { get; set; }
        public bool IsPriority { get; set; }

        public void Disconnect(DisconnectReason reason, string details)
        {
        }

        public Task<OwnedBlockBodies> GetBlockBodies(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
        {
            BlockBody[] result = new BlockBody[blockHashes.Count];
            for (int i = 0; i < blockHashes.Count; i++)
            {
                Block? block = _remoteTree.FindBlock(blockHashes[i], BlockTreeLookupOptions.RequireCanonical);
                result[i] = new BlockBody(block?.Transactions, block?.Uncles);
            }

            return Task.FromResult(new OwnedBlockBodies(result));
        }

        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(Hash256 blockHash, int maxBlocks, int skip, CancellationToken token)
        {
            ArrayPoolList<BlockHeader> result = new ArrayPoolList<BlockHeader>(maxBlocks, maxBlocks);
            long? firstNumber = _remoteTree.FindHeader(blockHash, BlockTreeLookupOptions.RequireCanonical)?.Number;
            if (!firstNumber.HasValue)
            {
                return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(result);
            }

            for (int i = 0; i < maxBlocks; i++)
            {
                result[i] = _remoteTree.FindHeader(firstNumber.Value + i + skip, BlockTreeLookupOptions.RequireCanonical)!;
            }

            return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(result);
        }

        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
        {
            ArrayPoolList<BlockHeader> result = new ArrayPoolList<BlockHeader>(maxBlocks, maxBlocks);
            long? firstNumber = _remoteTree.FindHeader(number, BlockTreeLookupOptions.RequireCanonical)?.Number;
            if (!firstNumber.HasValue)
            {
                return Task.FromResult<IOwnedReadOnlyList<BlockHeader>>(result)!;
            }

            for (int i = 0; i < maxBlocks; i++)
            {
                long blockNumber = firstNumber.Value + i + skip;
                if (blockNumber > (_remoteTree.Head?.Number ?? 0))
                {
                    result[i] = null!;
                }
                else
                {
                    result[i] = _remoteTree.FindBlock(blockNumber, BlockTreeLookupOptions.None)!.Header;
                }
            }

            return Task.FromResult<IOwnedReadOnlyList<BlockHeader>>(result)!;
        }

        public Task<BlockHeader?> GetHeadBlockHeader(Hash256? hash, CancellationToken token)
        {
            return Task.FromResult(_remoteTree.Head?.Header);
        }

        private readonly BlockingCollection<Action> _sendQueue = new();

        public void NotifyOfNewBlock(Block block, SendBlockMode mode)
        {
            if (mode == SendBlockMode.FullBlock)
            {
                SendNewBlock(block);
            }
            else
            {
                HintNewBlock(block.Hash!, block.Number);
            }
        }

        private void SendNewBlock(Block block)
        {
            _sendQueue.Add(() => _remoteSyncServer?.AddNewBlock(block, this));
        }

        private void HintNewBlock(Hash256 blockHash, long number)
        {
            _sendQueue.Add(() => _remoteSyncServer?.HintBlock(blockHash, number, this));
        }

        public PublicKey Id => Node.Id;

        public void SendNewTransactions(IEnumerable<Transaction> txs, bool sendFullTx) { }

        public Task<IOwnedReadOnlyList<TxReceipt[]?>> GetReceipts(IReadOnlyList<Hash256> blockHash, CancellationToken token)
        {
            TxReceipt[]?[] result = new TxReceipt[blockHash.Count][];
            for (int i = 0; i < blockHash.Count; i++)
            {
                result[i] = _remoteSyncServer?.GetReceipts(blockHash[i])!;
            }

            return Task.FromResult<IOwnedReadOnlyList<TxReceipt[]?>>(result.ToPooledList());
        }

        public Task<IOwnedReadOnlyList<byte[]>> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token) => Task.FromResult(_remoteSyncServer?.GetNodeData(hashes, token))!;

        public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class
        {
            throw new NotImplementedException();
        }

        public bool TryGetSatelliteProtocol<T>(string protocol, out T protocolHandler) where T : class
        {
            throw new NotImplementedException();
        }

        public Task Close()
        {
            _sendQueue.CompleteAdding();
            return _closeTaskCompletionSource.Task;
        }
    }
}
