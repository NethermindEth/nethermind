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
// 

using System.Collections.Concurrent;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Data
{
    public class PayloadManager
    {
        private readonly IBlockTree _blockTree;
        private readonly LruCache<Keccak, bool> _executePayloadHashes;
        private readonly LruCache<Keccak, bool> _consensusValidatedResults;
        private readonly LruCache<Keccak, Block> _pendingValidPayloads;
        
        /// <summary>
        /// Number of executePayload hashes and consensusValidated hashes stored in cache.
        /// </summary>
        private const int CacheSize = 300;

        /// <summary>
        /// Max number of valid payloads stored and waiting for consensusValidated message.
        /// </summary>
        private const int PayloadStorageSize = 100;

        public PayloadManager(IBlockTree blockTree)
        {
            _blockTree = blockTree;
            _executePayloadHashes = new LruCache<Keccak, bool>(CacheSize, "Recent Execute Payload Hashes");
            _consensusValidatedResults = new LruCache<Keccak, bool>(CacheSize, "Recent Consensus Validated Results");
            _pendingValidPayloads = new LruCache<Keccak, Block>(PayloadStorageSize, "Stored Pending Valid Payloads");
        }

        public void TryAddPayloadBlockHash(Keccak blockhash)
        {
            if (!_executePayloadHashes.TryGet(blockhash, out _))
            {
                _executePayloadHashes.Set(blockhash, false);
            }
        }

        public void MarkPayloadValidationAsFinished(Keccak blockhash)
        {
            if (!_executePayloadHashes.Get(blockhash))
            {
                _executePayloadHashes.Set(blockhash, true);
            }
        }

        public bool CheckIfExecutePayloadIsFinished(Keccak blockhash, out bool isFinished)
        {
            if (_executePayloadHashes.TryGet(blockhash, out isFinished))
            {
                return true;
            }

            return false;
        }

        public bool TryAddConsensusValidatedResult(Keccak blockhash, bool isValid)
        {
            if (_consensusValidatedResults.TryGet(blockhash, out _))
            {
                return false;
            }
            
            _consensusValidatedResults.Set(blockhash, isValid);
            return true;
        }

        public bool CheckConsensusValidatedResult(Keccak blockhash, out bool isValid)
        {
            if (_consensusValidatedResults.TryGet(blockhash, out isValid))
            {
                return true;
            }

            return false;
        }
        
        public bool TryAddValidPayload(Keccak blockhash, Block block)
        {
            if (_pendingValidPayloads.TryGet(blockhash, out _))
            {
                return false;
            }
            
            _pendingValidPayloads.Set(blockhash, block);
            return true;
        }

        public void ProcessValidatedPayload(Keccak blockhash)
        {
            if (_pendingValidPayloads.TryGet(blockhash, out Block block))
            {
                ProcessValidatedPayload(block);
                _pendingValidPayloads.Delete(blockhash);
            }
        }
        
        public void ProcessValidatedPayload(Block block)
        {
            _blockTree.SuggestBlock(block, true, null, true);
        }
    }
}
