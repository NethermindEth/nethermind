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
using System.IO;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.Baseline.Tree
{
    public class BaselineTreeMetadata
    {
        private const long CurrentBlockIndex = -1;

        private readonly IKeyValueStore _metadataKeyValueStore;
        public byte[] DbPrefix { get; }
        private readonly ILogger _logger;

        public BaselineTreeMetadata(IKeyValueStore metadataKeyValueStore, byte[] _dbPrefix, ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metadataKeyValueStore = metadataKeyValueStore ?? throw new ArgumentNullException(nameof(metadataKeyValueStore));
            this.DbPrefix = _dbPrefix;
        }

        public uint GetBlockCount(long lastBlockWithLeaves, long blockNumber)
        {
            if (_logger.IsWarn)
                _logger.Warn(
                    $"Getting block count for {DbPrefix.ToHexString()}");

            if (blockNumber == 0 || lastBlockWithLeaves == 0)
            {
                return 0;
            }

            (uint Count, long PreviousBlockWithLeaves) foundCount = LoadBlockNumberCount(lastBlockWithLeaves);
            if (_logger.IsWarn)
                _logger.Warn(
                    $"Starting with ({foundCount.Count},{foundCount.PreviousBlockWithLeaves}) for {DbPrefix.ToHexString()}");
            long currentBlockNumber = lastBlockWithLeaves;
            while (blockNumber < currentBlockNumber)
            {
                if (foundCount.PreviousBlockWithLeaves == 0)
                {
                    if (_logger.IsWarn)
                        _logger.Warn(
                            $"Reached zero and found count 0 for {DbPrefix.ToHexString()}");
                    return 0;
                }

                currentBlockNumber = foundCount.PreviousBlockWithLeaves;

                foundCount = LoadBlockNumberCount(foundCount.PreviousBlockWithLeaves);
                if (_logger.IsWarn)
                    _logger.Warn(
                        $"Jumped to ({foundCount.Count},{foundCount.PreviousBlockWithLeaves}) for {DbPrefix.ToHexString()}");
            }

            if (_logger.IsWarn)
                _logger.Warn(
                    $"Found count {foundCount.Count} for {DbPrefix.ToHexString()}");

            return foundCount.Count;
        }

        public (uint Count, long NewLastBlockWithLeaves) GoBackTo(long blockNumber, long lastBlockWithLeaves)
        {
            (uint Count, long NewLastBlockWithLeaves) result;
            while (lastBlockWithLeaves > blockNumber)
            {
                _logger.Warn($"Loading and clearing {lastBlockWithLeaves} when reorganizing to block:{blockNumber} and last non-empty:{lastBlockWithLeaves}.");
                result = LoadBlockNumberCount(lastBlockWithLeaves);
                ClearBlockNumberCount(lastBlockWithLeaves);
                lastBlockWithLeaves = result.NewLastBlockWithLeaves;
            }

            if (lastBlockWithLeaves == blockNumber)
            {
                result = LoadBlockNumberCount(lastBlockWithLeaves);
            }
            else
            {
                result = LoadBlockNumberCount(lastBlockWithLeaves);
                result.NewLastBlockWithLeaves = lastBlockWithLeaves;
            }
            
            return result;
        }

        private byte[] MetadataBuildDbKey(long blockNumber)
        {
            return Rlp.Encode(Rlp.Encode(DbPrefix), Rlp.Encode(blockNumber)).Bytes;
        }

        private readonly (uint, long) NoCount = (0u, 0L);

        public (uint Count, long PreviousBlockWithLeaves) LoadBlockNumberCount(long blockNumber)
        {
            byte[]? data = _metadataKeyValueStore[MetadataBuildDbKey(blockNumber)];
            (uint, long) result;
            if (data == null)
            {
                result = NoCount;
            }
            else
            {
                RlpStream? rlpStream = new RlpStream(data);
                rlpStream.SkipLength();

                result = (rlpStream.DecodeUInt(), rlpStream.DecodeLong());
            }

            if (_logger.IsWarn)
                _logger.Warn(
                    $"Loading count for block {blockNumber} in {DbPrefix.ToHexString()} - ({result.Item1},{result.Item2})");
            return result;
        }

        internal void SaveBlockNumberCount(long blockNumber, uint count, long previousBlockWithLeaves)
        {
            if (blockNumber == 0)
            {
                return;
            }
            
            if (blockNumber <= previousBlockWithLeaves)
            {
                throw new InvalidDataException($"Trying to save {blockNumber}->{previousBlockWithLeaves} (current->previous)");
            }
            
            if (_logger.IsWarn)
                _logger.Warn(
                    $"Saving count for block {blockNumber} in {DbPrefix.ToHexString()} - ({count},{previousBlockWithLeaves})");

            int length = Rlp.LengthOfSequence(Rlp.LengthOf((long) count) + Rlp.LengthOf(previousBlockWithLeaves));
            RlpStream rlpStream = new RlpStream(length);
            rlpStream.StartSequence(length);
            rlpStream.Encode(count);
            rlpStream.Encode(previousBlockWithLeaves);
            _metadataKeyValueStore[MetadataBuildDbKey(blockNumber)] = rlpStream.Data;
        }

        public (Keccak LastBlockDbHash, long LastBlockWithLeaves) LoadCurrentBlockInDb()
        {
            byte[]? rlpEncoded = _metadataKeyValueStore[MetadataBuildDbKey(CurrentBlockIndex)];
            (Keccak, long) result;
            if (rlpEncoded == null)
            {
                result = (Keccak.Zero, 0);
            }
            else
            {
                RlpStream? rlpStream = new RlpStream(rlpEncoded);
                rlpStream.SkipLength();
                result = (rlpStream.DecodeKeccak(), rlpStream.DecodeLong());
            }

            if (_logger.IsWarn) _logger.Warn($"Loaded count of {DbPrefix.ToHexString()} at block {CurrentBlockIndex}");
            return result;
        }

        internal void SaveCurrentBlockInDb(Keccak lastBlockDbHash, long lastBlockWithLeaves)
        {
            if (_logger.IsWarn)
                _logger.Warn(
                    $"Saving current block {lastBlockWithLeaves}|{lastBlockDbHash} of tree {DbPrefix.ToHexString()}");
            int length = Rlp.LengthOfSequence(Rlp.LengthOf(lastBlockDbHash) + Rlp.LengthOf(lastBlockWithLeaves));
            RlpStream rlpStream = new RlpStream(length);
            rlpStream.StartSequence(length);
            rlpStream.Encode(lastBlockDbHash);
            rlpStream.Encode(lastBlockWithLeaves);
            _metadataKeyValueStore[MetadataBuildDbKey(CurrentBlockIndex)] = rlpStream.Data;
        }

        private void ClearBlockNumberCount(long blockNumber)
        {
            if (_logger.IsWarn)
                _logger.Warn(
                    $"Clearing count at block {blockNumber} at {DbPrefix.ToHexString()}");
            _metadataKeyValueStore[MetadataBuildDbKey(blockNumber)] = null;
        }
    }
}