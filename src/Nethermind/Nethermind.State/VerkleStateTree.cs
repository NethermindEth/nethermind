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
using System.Reflection.Metadata;
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
            byte[][] TreeKeys = GetTreeKeysForAccount(address);
            
            // byte[]? bytes = _get(ValueKeccak.Compute(address.Bytes).BytesAsSpan);
            // if (bytes is null)
            // {
            //     return null;
            // }
            byte[] version = _get(TreeKeys[0]);
            byte[] balance = _get(TreeKeys[1]);
            byte[] nonce = _get(TreeKeys[2]);
            byte[] codeKeccak = _get(TreeKeys[3]);
            byte[] codeSize = _get(TreeKeys[4]);
            Account account = new (
                
                new UInt256(balance.AsSpan(), true),
                new UInt256(nonce.AsSpan(), true),
                new Keccak(codeKeccak),
                new UInt256(codeSize.AsSpan(), true),
                new UInt256(version.AsSpan(), true)
                );

            return account;
        }
        

        private static readonly Rlp EmptyAccountRlp = Rlp.Encode(Account.TotallyEmpty);

        public void Set(Address address, Account? account)
        {
            byte[][] TreeKeys = GetTreeKeysForAccount(address);
            if (account is null)
            {
                account = Account.TotallyEmpty;
            }
            _set(TreeKeys[0], account.Version.ToBigEndian());
            _set(TreeKeys[1], account.Balance.ToBigEndian());
            _set(TreeKeys[2], account.Nonce.ToBigEndian());
            _set(TreeKeys[3], account.CodeHash.Bytes);
            _set(TreeKeys[4], account.CodeSize.ToBigEndian());
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

        private byte[] GetTreeKeyPrefix(Address address, UInt256 treeIndex)
        {
             // is it guaranteed that the its a 12 length byte array initialized with zeros?
             byte[] addressPadding = new byte[12] ;
             IEnumerable<byte> treeKeyPrecursor = addressPadding.Concat(address.Bytes);
             treeKeyPrecursor = treeKeyPrecursor.Concat(treeIndex.ToBigEndian());
             return Sha2.Compute(treeKeyPrecursor.ToArray());
        }
        
         private byte[] GetTreeKey(Address address, UInt256 treeIndex , byte subIndexBytes)
         {
            
             byte[] treeKeyPrefix = GetTreeKeyPrefix(address, treeIndex);

             byte[] treeKey = new byte[32];
             Buffer.BlockCopy(treeKeyPrefix, 0, treeKey, 0, 31);
             treeKey[31] = subIndexBytes;
             return treeKey;
         }

         private byte[][] GetTreeKeysForAccount(Address address)
         {
             byte[] treeKeyPrefix = GetTreeKeyPrefix(address, 0);
             
             byte[] treeKeyVersion = new byte[32];
             Buffer.BlockCopy(treeKeyPrefix, 0, treeKeyVersion, 0, 31);
             treeKeyVersion[31] = VersionLeafKey;
             
             byte[] treeKeyBalance = new byte[32];
             Buffer.BlockCopy(treeKeyPrefix, 0, treeKeyBalance, 0, 31);
             treeKeyVersion[31] = BalanceLeafKey;
             
             byte[] treeKeyNounce = new byte[32];
             Buffer.BlockCopy(treeKeyPrefix, 0, treeKeyNounce, 0, 31);
             treeKeyVersion[31] = NonceLeafKey;
             
             byte[] treeKeyCodeKeccak = new byte[32];
             Buffer.BlockCopy(treeKeyPrefix, 0, treeKeyCodeKeccak, 0, 31);
             treeKeyVersion[31] = CodeKeccakLeafKey;
             
             byte[] treeKeyCodeSize = new byte[32];
             Buffer.BlockCopy(treeKeyPrefix, 0, treeKeyCodeSize, 0, 31);
             treeKeyVersion[31] = CodeSizeLeafKey;
            
             return new [] {treeKeyVersion, treeKeyBalance, treeKeyNounce, treeKeyCodeKeccak, treeKeyCodeSize};
         }
         
         
        
        // private byte[] GetTreeKeyForAccountLeaf(Address address, byte leaf)
        // {
        //     return GetTreeKey(address, UInt256.Zero, leaf);
        // }

        private byte[] GetTreeKeyForVersion(Address address)
        {
            return GetTreeKey(address, UInt256.Zero, VersionLeafKey);
        }

        private byte[] GetTreeKeyForBalance(Address address)
        {
            return GetTreeKey(address, UInt256.Zero, BalanceLeafKey);
        }

        private byte[] GetTreeKeyForNonce(Address address)
        {
            return GetTreeKey(address, UInt256.Zero, NonceLeafKey);
        }

        private byte[] GetTreeKeyForCodeKeccak(Address address)
        {
            return GetTreeKey(address, UInt256.Zero, CodeKeccakLeafKey);
        }

        private byte[] GetTreeKeyForCodeSize(Address address)
        {
            return GetTreeKey(address, UInt256.Zero, CodeSizeLeafKey);
        }
        
        private byte[] GetTreeKeyForCodeChunk(Address address, UInt256 chunk)
        {
            UInt256 chunkOffset = CodeOffset + chunk;
            
            UInt256 treeIndex = chunkOffset / VerkleNodeWidth;
            
            UInt256.Mod(chunkOffset, VerkleNodeWidth, out UInt256 subIndex);
            return GetTreeKey(address, treeIndex, subIndex.ToBigEndian()[0]);
        }

        private byte[] GetTreeKeyForStorageSlot(Address address, UInt256 storageKey)
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

        public void SetCode(Address address, byte[] code)
        {
            byte[][] chunkifiedCode = chunkifyCode(code);
            byte[] chunkKey;
            for (int i = 0; i < chunkifiedCode.Length; i++)
            {
                chunkKey = GetTreeKeyForCodeChunk(address, (UInt256)i);
                _set(chunkKey, chunkifiedCode[i]);
            }
        }
        
        private byte[][] chunkifyCode(byte[] code)
        {
            const int PUSH_OFFSET = 95;
            const int PUSH1 = PUSH_OFFSET + 1;
            const int PUSH32 = PUSH_OFFSET + 32;
            
            // To ensure that the code can be split into chunks of 32 bytes
            byte[] chunkifyableCode = new byte[code.Length + 31 - code.Length % 31];
            Buffer.BlockCopy(code, 0, chunkifyableCode, 0, code.Length);

            int[] bytesToExecData  = new int[chunkifyableCode.Length];
            int pos = 0;
            int pushLength;
            while (pos < chunkifyableCode.Length)
            {
                pushLength = 0;
                if ( PUSH1 <= chunkifyableCode[pos] && chunkifyableCode[pos] <= PUSH32)
                {
                    pushLength = chunkifyableCode[pos] - PUSH_OFFSET;
                }

                pos += 1;

                for (int i = 0; i < pushLength; i++)
                {
                    bytesToExecData[pos + i] = pushLength - i;
                }

                pos += pushLength;
            }
            
            int chunkCount = (chunkifyableCode.Length + 31) / 32;
            byte[][] chunks = new byte[chunkCount][];
            pos = 0;
            
            for (int i = 0; i < chunkCount; i++)
            {
                chunks[i] = new byte[32];
                chunks[i][0] = (byte)Math.Min(bytesToExecData[pos], 31);
                Buffer.BlockCopy(chunkifyableCode, pos, chunks[i], 1, 31);
                pos = pos + 31;
            }

            return chunks;
        }
        
    }
}
