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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    // TODO: work in progress
    public class BlockStore : IBlockStore
    {
        private readonly IBlockHeaderValidator _headerValidator;
        private readonly IBlockValidator _blockValidator;

        public BlockStore(IBlockHeaderValidator headerValidator, IBlockValidator blockValidator)
        {
            _headerValidator = headerValidator;
            _blockValidator = blockValidator;
        }

        private readonly Dictionary<Keccak, Block> _branches = new Dictionary<Keccak, Block>();
        private readonly Dictionary<Keccak, Block> _mainChain = new Dictionary<Keccak, Block>();

        private readonly Dictionary<Keccak, BlockInfo> _storedBlocks = new Dictionary<Keccak, BlockInfo>();

        public BlockInfo AddBlockHeader(BlockHeader blockHeader)
        {
            if (!_storedBlocks.ContainsKey(blockHeader.Hash))
            {
                HintBlock(blockHeader.Hash, blockHeader.Number);
            }

            _storedBlocks.TryGetValue(blockHeader.Hash, out BlockInfo blockInfo);
            if (blockInfo.HeaderQuality != Quality.Unknown)
            {
                return blockInfo;
            }
                       
            bool isValid = _headerValidator.Validate(blockHeader);
            blockInfo.HeaderQuality = isValid ? Quality.DataValid : Quality.Invalid;
            blockInfo.BlockHeader = blockHeader;
            blockInfo.HeaderLocation = BlockDataLocation.Memory;

            return blockInfo;
        }

        public BlockInfo AddBlock(Block block, PublicKey receivedFrom)
        {
            if (!_storedBlocks.ContainsKey(block.Hash))
            {
                HintBlock(block.Hash, block.Number);
            }
            
             _storedBlocks.TryGetValue(block.Hash, out BlockInfo blockInfo);
            if (blockInfo.BlockQuality != Quality.Unknown)
            {
                return blockInfo;
            }
                       
            bool isValid = _blockValidator.ValidateSuggestedBlock(block);
            blockInfo.HeaderQuality = isValid ? Quality.DataValid : Quality.Invalid;
            blockInfo.BlockHeader = block.Header;
            blockInfo.HeaderLocation = BlockDataLocation.Memory;
            blockInfo.Block = block;
            blockInfo.BodyLocation = BlockDataLocation.Memory;
            blockInfo.ReceivedFrom = receivedFrom;

            return blockInfo;
        }

        public void HintBlock(Keccak hash, BigInteger number)
        {
            if (!_storedBlocks.ContainsKey(hash))
            {
                _storedBlocks[hash] = new BlockInfo(hash, number);
            }
        }

        public Block Load(Keccak hash)
        {
            if (!_storedBlocks.ContainsKey(hash))
            {
                throw new InvalidOperationException("Trying to load an unknown block.");
            }

            BlockInfo blockInfo = _storedBlocks[hash];
            if (blockInfo.BodyLocation == BlockDataLocation.Remote || blockInfo.HeaderLocation == BlockDataLocation.Remote)
            {
                throw new InvalidOperationException("Cannot load block that has not been synced yet.");
            }

            if (blockInfo.BodyLocation == BlockDataLocation.Store || blockInfo.HeaderLocation == BlockDataLocation.Store)
            {
                throw new NotImplementedException("Block persistence not implemented yet");
            }

            return blockInfo.Block;
        }

        public void MarkProcessed(Keccak hash, bool isValid)
        {
            if (!_storedBlocks.ContainsKey(hash))
            {
                throw new InvalidOperationException("Trying to mark an unknown block as processed.");
            }

            BlockInfo blockInfo = _storedBlocks[hash];
            blockInfo.BlockQuality = isValid ? Quality.Processed : Quality.Invalid;
        }

        public void AddBlock(Block block, bool isMainChain)
        {
            if (isMainChain)
            {
                _mainChain.Add(block.Header.Hash, block);
                _processed.Add(block.Header.Hash);
            }
            else
            {
                _branches.Add(block.Header.Hash, block);
            }
        }

        public Block FindBlock(Keccak blockHash, bool mainChainOnly)
        {
            _mainChain.TryGetValue(blockHash, out Block block);
            if (block == null && !mainChainOnly)
            {
                _branches.TryGetValue(blockHash, out block);
            }

            return block;
        }

        public Block FindBlock(BigInteger blockNumber)
        {
            return _mainChain.Values.FirstOrDefault(x => x.Header?.Number == blockNumber);
        }

        public bool IsMainChain(Keccak blockHash)
        {
            return _mainChain.ContainsKey(blockHash);
        }

        public void MoveToBranch(Keccak blockHash)
        {
            _branches.Add(blockHash, _mainChain[blockHash]);
            _mainChain.Remove(blockHash);
        }

        private readonly HashSet<Keccak> _processed = new HashSet<Keccak>();

        public bool WasProcessed(Keccak blockHash)
        {
            return _processed.Contains(blockHash);
        }

        public void MoveToMain(Keccak blockHash)
        {
            _mainChain.Add(blockHash, _branches[blockHash]);
            _processed.Add(blockHash);
            _branches.Remove(blockHash);
        }
    }
}