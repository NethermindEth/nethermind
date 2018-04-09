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
using System.Numerics;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain
{
    public class SynchronizationManager : ISynchronizationManager
    {
        private readonly IHeaderValidator _headerValidator;
        private readonly IBlockValidator _blockValidator;
        private readonly ITransactionValidator _transactionValidator;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;
        private readonly Dictionary<Keccak, BlockInfo> _storedBlocks = new Dictionary<Keccak, BlockInfo>();

        public SynchronizationManager(IHeaderValidator headerValidator, IBlockValidator blockValidator, ITransactionValidator transactionValidator, ISpecProvider specProvider, Block genesisBlock, ILogger logger)
        {
            _headerValidator = headerValidator;
            _blockValidator = blockValidator;
            _transactionValidator = transactionValidator;
            _specProvider = specProvider;
            _logger = logger;
            BlockInfo blockInfo = AddBlock(genesisBlock, new PublicKey(new byte[64]));
            if (blockInfo.BlockQuality == Quality.Invalid)
            {
                throw new EthSynchronizationException("Provided genesis block is not valid");
            }
            
            _logger.Log($"Initialized {nameof(SynchronizationManager)} with genesis block {genesisBlock.Hash}");
        }

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

        public BlockInfo Find(Keccak hash)
        {
            if (_storedBlocks.ContainsKey(hash))
            {
                return _storedBlocks[hash];
            }

            return null;
        }

        public BlockInfo Find(BigInteger number)
        {
            throw new NotImplementedException();
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

        private readonly Dictionary<Keccak, TransactionInfo> _transactions = new Dictionary<Keccak, TransactionInfo>();

        public TransactionInfo Add(Transaction transaction, PublicKey receivedFrom)
        {
            _transactions.TryGetValue(transaction.Hash, out TransactionInfo info);
            if (info == null)
            {
                info = new TransactionInfo(transaction, receivedFrom);
                info.Quality = _transactionValidator.IsWellFormed(transaction, _specProvider.GetCurrentSpec()) ? Quality.DataValid : Quality.Invalid;
            }

            return info;
        }

        public void MarkAsProcessed(Transaction transaction, bool isValid)
        {
            _transactions.TryGetValue(transaction.Hash, out TransactionInfo info);
            if (info != null)
            {
                info.Quality = isValid ? Quality.Processed : Quality.Invalid;
            }
        }

        public void MarkAsProcessed(Block transaction, bool isValid)
        {
            _storedBlocks.TryGetValue(transaction.Hash, out BlockInfo info);
            if (info != null)
            {
                info.BlockQuality = isValid ? Quality.Processed : Quality.Invalid;
            }
        }
    }
}