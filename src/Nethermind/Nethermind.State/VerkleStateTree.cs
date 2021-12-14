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
using System.Diagnostics;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Metrics = Nethermind.Db.Metrics;

namespace Nethermind.State
{
    public class VerkleStateTree
    {
        
        private readonly AccountDecoder _decoder = new();
        
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

        private readonly IntPtr _verkleTrieObj;
        
        public static readonly UInt256 EmptyTreeHash = UInt256.Zero;
        public TrieType TrieType { get; protected set; }
        
        private UInt256 _rootHash = UInt256.Zero;
        
        private readonly bool _allowCommits;

        public UInt256 RootHash;

        public VerkleStateTree()
            : this(EmptyTreeHash, true, NullLogManager.Instance)
        {
        }
        public VerkleStateTree(ILogManager? logManager)
            : this(EmptyTreeHash, true, logManager)
        {
        }

        public VerkleStateTree(
            UInt256 rootHash,
            bool allowCommits,
            ILogManager? logManager)
        {
            // TODO: do i need to pass roothash here to rust to use for initialization of the library?
            _verkleTrieObj = RustVerkleLib.VerkleTrieNew();
            
            _logger = logManager?.GetClassLogger<VerkleTrie>() ?? throw new ArgumentNullException(nameof(logManager));
            _allowCommits = allowCommits;
            RootHash = rootHash;
            MainStorageOffsetBase.LeftShift(MainStorageOffsetExponent, out MainStorageOffset);
        }
        
        [DebuggerStepThrough]
        public Account? Get(Address address, Keccak? rootHash = null)
        {
            // byte[]? bytes = _get(ValueKeccak.Compute(address.Bytes).BytesAsSpan, rootHash);
            byte[]? bytes = _get(ValueKeccak.Compute(address.Bytes).BytesAsSpan);
            if (bytes is null)
            {
                return null;
            }

            return _decoder.Decode(bytes.AsRlpStream());
        }
        
        [DebuggerStepThrough]
        internal Account? Get(Keccak keccak) // for testing
        {
            byte[]? bytes = _get(keccak.Bytes);
            if (bytes is null)
            {
                return null;
            }

            return _decoder.Decode(bytes.AsRlpStream());
        }

        private static readonly Rlp EmptyAccountRlp = Rlp.Encode(Account.TotallyEmpty);

        public void Set(Address address, Account? account)
        {
            ValueKeccak keccak = ValueKeccak.Compute(address.Bytes);
            _set(keccak.BytesAsSpan, account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account));
        }
        
        [DebuggerStepThrough]
        internal void Set(Keccak keccak, Account? account) // for testing
        {
            _set(keccak.Bytes, account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account));
        }


        [DebuggerStepThrough]
        // TODO: add functionality to start with a given root hash (traverse from a starting node)
        // public byte[]? Get(Span<byte> rawKey, Keccak? rootHash = null)
        public byte[]? _get(Span<byte> rawKey)
        {
            byte[] result = RustVerkleLib.VerkleTrieGet(_verkleTrieObj, rawKey.ToArray());
            return result;
        }
        
        
        [DebuggerStepThrough]
        public void _set(Span<byte> rawKey, byte[] value)
        {
            if (_logger.IsTrace)
                _logger.Trace($"{(value.Length == 0 ? $"Deleting {rawKey.ToHexString()}" : $"Setting {rawKey.ToHexString()} = {value.ToHexString()}")}");
            // TODO; error handling here? or at least a way to check if the operation was successful
            RustVerkleLib.VerkleTrieInsert(_verkleTrieObj, rawKey.ToArray(), value);
        }
        
        public void _set(Span<byte> rawKey, Rlp? value)
        {
            _set(rawKey, value is null ? Array.Empty<byte>() : value.Bytes);
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
