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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.State
{
    public class VerkleStateProvider
    {
        private const int VersionLeafKey = 0;
        private const int BalanceLeafKey = 1;
        private const int NonceLeafKey = 2;
        private const int CodeKeccakLeafKey = 3;
        private const int CodeSizeLeafKey = 4;
        
        private readonly UInt256 HeaderStorageOffset = 64;
        private readonly UInt256 CodeOffset = 128;
        private readonly UInt256 VerkleNodeWidth = 256;
        
        private readonly UInt256 MainStorageOffsetBase = 256;
        private const int MainStorageOffsetExponent = 31;
        private readonly UInt256 MainStorageOffset;
        
        private readonly ILogger _logger;
        
        public VerkleStateProvider(ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<StateProvider>() ?? throw new ArgumentNullException(nameof(logManager));
            // TODO: move this calculation out of here
            MainStorageOffsetBase.LeftShift(MainStorageOffsetExponent, out MainStorageOffset);
        }
        
        public byte[] GetTreeKey(Address address, UInt256 treeIndex , byte subIndexBytes)
        {   
            // is it guaranteed that the its a 12 length byte array initialized with zeros?
            byte[] addressPadding = new byte[12] ;
            IEnumerable<byte> treeKeyPrecursor = addressPadding.Concat(address.Bytes);
            treeKeyPrecursor = treeKeyPrecursor.Concat(treeIndex.ToBigEndian());

            byte[] treeKey = new byte[32];
            Buffer.BlockCopy(Sha2.Compute(treeKeyPrecursor.ToArray()), 0, treeKey, 0, 31);
            treeKey[31] = subIndexBytes;
            return treeKey;
        }
        
        public byte[] GetTreeKeyForAccountLeaf(Address address, byte leaf)
        {
            return GetTreeKey(address, UInt256.Zero, leaf);
        }

        public byte[] GetTreeKeyForVersion(Address address)
        {
            return GetTreeKey(address, UInt256.Zero, VersionLeafKey);
        }

        public byte[] GetTreeKeyForBalance(Address address)
        {
            return GetTreeKey(address, UInt256.Zero, BalanceLeafKey);
        }

        public byte[] GetTreeKeyForNonce(Address address)
        {
            return GetTreeKey(address, UInt256.Zero, NonceLeafKey);
        }

        public byte[] GetTreeKeyForCodeKeccak(Address address)
        {
            return GetTreeKey(address, UInt256.Zero, CodeKeccakLeafKey);
        }

        public byte[] GetTreeKeyForCodeSize(Address address)
        {
            return GetTreeKey(address, UInt256.Zero, CodeSizeLeafKey);
        }
        
        public byte[] GetTreeKeyForCodeChunk(Address address, UInt256 chunk)
        {
            UInt256 chunkOffset = CodeOffset + chunk;
            
            UInt256 treeIndex = chunkOffset / VerkleNodeWidth;
            
            UInt256.Mod(chunkOffset, VerkleNodeWidth, out UInt256 subIndex);
            return GetTreeKey(address, treeIndex, subIndex.ToBigEndian()[0]);
        }

        public byte[] GetTreeKeyForStorageSlot(Address address, UInt256 storageKey)
        {
            UInt256 pos;
            
            if (storageKey < CodeOffset - HeaderStorageOffset)
            {
                pos = HeaderStorageOffset + storageKey;
            } 
            else
            {
                pos = MainStorageOffset + storageKey;
            }

            UInt256 treeIndex = pos / VerkleNodeWidth;
            
            UInt256.Mod(pos, VerkleNodeWidth, out UInt256 subIndex);
            return GetTreeKey(address, treeIndex, subIndex.ToBigEndian()[0]);
        }
        
    }
    
}
