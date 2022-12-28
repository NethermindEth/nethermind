// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State.Proofs;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks
{
    public class BlockDownloadContext
    {
        private readonly Dictionary<int, int> _indexMapping;
        private readonly ISpecProvider _specProvider;
        private readonly PeerInfo _syncPeer;
        private readonly bool _downloadReceipts;
        private readonly IReceiptsRecovery _receiptsRecovery;

        public BlockDownloadContext(ISpecProvider specProvider, PeerInfo syncPeer, BlockHeader?[] headers,
            bool downloadReceipts, IReceiptsRecovery receiptsRecovery)
        {
            _indexMapping = new Dictionary<int, int>();
            _downloadReceipts = downloadReceipts;
            _receiptsRecovery = receiptsRecovery;
            _specProvider = specProvider;
            _syncPeer = syncPeer;

            Blocks = new Block[headers.Length - 1];
            NonEmptyBlockHashes = new List<Keccak>();

            if (_downloadReceipts)
            {
                ReceiptsForBlocks = new TxReceipt[Blocks.Length][]; // do that only if downloading receipts
            }

            int currentBodyIndex = 0;
            for (int i = 1; i < headers.Length; i++)
            {
                BlockHeader? header = headers[i];
                if (header?.Hash is null)
                {
                    break;
                }

                if (header.HasBody)
                {
                    Blocks[i - 1] = new Block(header);
                    _indexMapping.Add(currentBodyIndex, i - 1);
                    currentBodyIndex++;
                    NonEmptyBlockHashes.Add(header.Hash);
                }
                else
                {
                    Blocks[i - 1] = new Block(header);
                }
            }
        }

        public int FullBlocksCount => Blocks.Length;

        public Block[] Blocks { get; }

        public TxReceipt[]?[]? ReceiptsForBlocks { get; }

        public List<Keccak> NonEmptyBlockHashes { get; }

        public IReadOnlyList<Keccak> GetHashesByOffset(int offset, int maxLength)
        {
            var hashesToRequest =
                offset == 0
                    ? NonEmptyBlockHashes
                    : NonEmptyBlockHashes.Skip(offset);

            if (maxLength < NonEmptyBlockHashes.Count - offset)
            {
                hashesToRequest = hashesToRequest.Take(maxLength);
            }

            return hashesToRequest.ToList();
        }

        public void SetBody(int index, BlockBody body)
        {
            int mappedIndex = _indexMapping[index];
            Block block = Blocks[mappedIndex];
            if (body is null)
            {
                throw new EthSyncException($"{_syncPeer} sent an empty body for {block.ToString(Block.Format.Short)}.");
            }

            Blocks[mappedIndex] = block.WithReplacedBody(body);
        }

        public bool TrySetReceipts(int index, TxReceipt[]? receipts, out Block block)
        {
            if (!_downloadReceipts)
            {
                throw new InvalidOperationException($"Unexpected call to {nameof(TrySetReceipts)} when not downloading receipts");
            }

            int mappedIndex = _indexMapping[index];
            block = Blocks[_indexMapping[index]];
            receipts ??= Array.Empty<TxReceipt>();

            bool result = _receiptsRecovery.TryRecover(block, receipts, false) != ReceiptsRecoveryResult.Fail;
            if (result)
            {
                ValidateReceipts(block, receipts);
                ReceiptsForBlocks![mappedIndex] = receipts;
            }

            return result;
        }

        public Block GetBlockByRequestIdx(int index)
        {
            int mappedIndex = _indexMapping[index];
            return Blocks[mappedIndex];
        }

        private void ValidateReceipts(Block block, TxReceipt[] blockReceipts)
        {
            Keccak receiptsRoot = new ReceiptTrie(_specProvider.GetSpec(block.Header), blockReceipts).RootHash;

            if (receiptsRoot != block.ReceiptsRoot)
            {
                throw new EthSyncException($"Wrong receipts root for downloaded block {block.ToString(Block.Format.Short)}.");
            }
        }
    }
}
