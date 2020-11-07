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
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.Baseline.Tree
{
    public class BaselineTreeMetadata
    {
        private const long CurrentBlockIndex = -1;

        private readonly IKeyValueStore _metadataKeyValueStore;
        private readonly byte[] _dbPrefix;
        public BaselineTreeMetadata(IKeyValueStore metadataKeyValueStore, byte[] _dbPrefix)
        {
            _metadataKeyValueStore = metadataKeyValueStore ?? throw new ArgumentNullException(nameof(metadataKeyValueStore));
            this._dbPrefix = _dbPrefix;
        }

        public uint GetLeavesCountFromPreviousBlock(long lastBlockWithLeaves, long blockNumber, bool clearPreviousCounts = false)
        {
            var currentBlockNumber = blockNumber;
            var foundCount = LoadBlockNumberCount(lastBlockWithLeaves);
            while (lastBlockWithLeaves <= currentBlockNumber)
            {
                currentBlockNumber = foundCount.PreviousBlockWithLeaves;
                if (currentBlockNumber == 0)
                {
                    return 0;
                }

                foundCount = LoadBlockNumberCount(foundCount.PreviousBlockWithLeaves);
                if (clearPreviousCounts)
                {
                    ClearBlockNumberCount(foundCount.PreviousBlockWithLeaves);
                }
            }

            return foundCount.Count;
        }

        private byte[] MetadataBuildDbKey(long blockNumber)
        {
            return Rlp.Encode(Rlp.Encode(_dbPrefix), Rlp.Encode(blockNumber)).Bytes;
        }

        public (uint Count, long PreviousBlockWithLeaves) LoadBlockNumberCount(long blockNumber)
        {
            var data = _metadataKeyValueStore[MetadataBuildDbKey(blockNumber)];
            var rlpStream = new RlpStream(data);
            rlpStream.SkipLength();
            return (rlpStream.DecodeUInt(), rlpStream.DecodeLong());
        }

        public void SaveBlockNumberCount(long blockNumber, uint count, long previousBlockWithLeaves)
        {
            var length = Rlp.LengthOfSequence(Rlp.LengthOf(count) + Rlp.LengthOf(previousBlockWithLeaves));
            RlpStream rlpStream = new RlpStream(length);
            rlpStream.StartSequence(length);
            rlpStream.Encode(count);
            rlpStream.Encode(previousBlockWithLeaves);
            _metadataKeyValueStore[MetadataBuildDbKey(blockNumber)] = rlpStream.Data;
        }

        public (Keccak LastBlockDbHash, long LastBlockWithLeaves) LoadCurrentBlockInDb()
        {
            var rlpEncoded = _metadataKeyValueStore[MetadataBuildDbKey(CurrentBlockIndex)];
            if (rlpEncoded == null)
                return (Keccak.Zero, 0);
            var rlpStream = new RlpStream(rlpEncoded);
            rlpStream.SkipLength();
            return (rlpStream.DecodeKeccak(), rlpStream.DecodeLong());
        }

        public void SaveCurrentBlockInDb(Keccak lastBlockDbHash, long lastBlockWithLeaves)
        {
            var length = Rlp.LengthOfSequence(Rlp.LengthOf(lastBlockDbHash) + Rlp.LengthOf(lastBlockWithLeaves));
            RlpStream rlpStream = new RlpStream(length);
            rlpStream.StartSequence(length);
            rlpStream.Encode(lastBlockDbHash);
            rlpStream.Encode(lastBlockWithLeaves);
            _metadataKeyValueStore[MetadataBuildDbKey(CurrentBlockIndex)] = rlpStream.Data;
        }

        private void ClearBlockNumberCount(long blockNumber)
        {
            _metadataKeyValueStore[MetadataBuildDbKey(blockNumber)] = null;
        }
    }
}
