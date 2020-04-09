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
    internal class BlockDownloadContext
    {
        private readonly Dictionary<int, int> _indexMapping;
        private readonly ISpecProvider _specProvider;
        private readonly PeerInfo _syncPeer;
        private readonly bool _downloadReceipts;
        private readonly IReceiptsRecovery _receiptsRecovery;

        public BlockDownloadContext(ISpecProvider specProvider, PeerInfo syncPeer, BlockHeader[] headers, bool downloadReceipts, IReceiptsRecovery receiptsRecovery)
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
                if (headers[i] == null)
                {
                    break;
                }

                if (headers[i].HasBody)
                {
                    Blocks[i - 1] = new Block(headers[i], (BlockBody) null);
                    _indexMapping.Add(currentBodyIndex, i - 1);
                    currentBodyIndex++;
                    NonEmptyBlockHashes.Add(headers[i].Hash);
                }
                else
                {
                    Blocks[i - 1] = new Block(headers[i], BlockBody.Empty);
                }
            }
        }

        public int FullBlocksCount => Blocks.Length;

        public Block[] Blocks { get; set; }

        public TxReceipt[][] ReceiptsForBlocks { get; private set; }

        public List<Keccak> NonEmptyBlockHashes { get; set; }

        public IList<Keccak> GetHashesByOffset(int offset, int maxLength)
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
            Block block = Blocks[_indexMapping[index]];
            if (body == null)
            {
                throw new EthSyncException($"{_syncPeer} sent an empty body for {block.ToString(Block.Format.Short)}.");
            }

            block.Body = body;
        }

        public void SetReceipts(int index, TxReceipt[] receipts)
        {
            if (!_downloadReceipts)
            {
                throw new InvalidOperationException($"Unexpected call to {nameof(SetReceipts)} when not downloading receipts");
            }

            int mappedIndex = _indexMapping[index];
            Block block = Blocks[_indexMapping[index]];
            if (receipts == null)
            {
                receipts = Array.Empty<TxReceipt>();
            }

            if (_receiptsRecovery.TryRecover(block, receipts))
            {
                ValidateReceipts(block, receipts);
                ReceiptsForBlocks[mappedIndex] = receipts;
            }
            else
            {
                throw new EthSyncException($"Missing receipts for block {block.ToString(Block.Format.Short)}.");
            }
        }

        private void ValidateReceipts(Block block, TxReceipt[] blockReceipts)
        {
            Keccak receiptsRoot = new ReceiptTrie(block.Number, _specProvider, blockReceipts).RootHash;
            if (receiptsRoot != block.ReceiptsRoot)
            {
                throw new EthSyncException($"Wrong receipts root for downloaded block {block.ToString(Block.Format.Short)}.");
            }
        }
    }
}