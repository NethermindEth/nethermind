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
using System.Collections.Concurrent;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class VerkleTree
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

    
    private readonly IntPtr _verkleTrieObj;
    private readonly ILogger _logger;
    
    public static readonly Keccak EmptyTreeHash = new (UInt256.Zero.ToBigEndian());
    public TrieType TrieType { get; protected set; }
    
    private readonly bool _allowCommits;

    public Keccak RootHash;
    
    private readonly ConcurrentQueue<Exception>? _commitExceptions;
    private readonly ConcurrentQueue<NodeCommitInfo>? _currentCommit;
    
    public VerkleTree()
        : this(EmptyTreeHash, true, NullLogManager.Instance)
    {
    }
    public VerkleTree(ILogManager? logManager)
        : this(EmptyTreeHash, true, logManager)
    {
    }

    public VerkleTree(
        Keccak rootHash,
        bool allowCommits,
        ILogManager? logManager)
    {
        // TODO: do i need to pass roothash here to rust to use for initialization of the library?
        _verkleTrieObj = RustVerkleLib.VerkleTrieNew();
            
        _logger = logManager?.GetClassLogger<VerkleTree>() ?? throw new ArgumentNullException(nameof(logManager));
        _logger.Info(_verkleTrieObj.ToString());
        _allowCommits = allowCommits;
        RootHash = rootHash;
        MainStorageOffsetBase.LeftShift(MainStorageOffsetExponent, out MainStorageOffset);
        
        if (_allowCommits)
        {
            _currentCommit = new ConcurrentQueue<NodeCommitInfo>();
            _commitExceptions = new ConcurrentQueue<Exception>();
        }
    }
    
    
    [DebuggerStepThrough]
    // TODO: add functionality to start with a given root hash (traverse from a starting node)
    // public byte[]? Get(Span<byte> rawKey, Keccak? rootHash = null)
    public byte[]? GetValue(Span<byte> rawKey)
    {
        byte[]? result = RustVerkleLib.VerkleTrieGet(_verkleTrieObj, rawKey.ToArray());
        return result;
    }
    
    public Span<byte> GetValueSpan(byte[] rawKey) => RustVerkleLib.VerkleTrieGetSpan(_verkleTrieObj, rawKey);


    [DebuggerStepThrough]
    public void SetValue(Span<byte> rawKey, byte[] value)
    {
        if (_logger.IsTrace)
            _logger.Trace($"{(value.Length == 0 ? $"Deleting {rawKey.ToHexString()}" : $"Setting {rawKey.ToHexString()} = {value.ToHexString()}")}");
        // TODO; error handling here? or at least a way to check if the operation was successful
        RustVerkleLib.VerkleTrieInsert(_verkleTrieObj, rawKey.ToArray(), value);
    }

    public byte[] GetTreeKeyPrefix(Address address, UInt256 treeIndex)
    {
         // is it guaranteed that the its a 12 length byte array initialized with zeros?
         byte[] keyPrefix = new byte[64];
         Array.Copy(address.Bytes, 0, keyPrefix, 12, 20);
         treeIndex.ToBigEndian(keyPrefix.AsSpan(32));
         return Sha2.Compute(keyPrefix);
    }
    
    public byte[]? GetValue(byte[] keyPrefix, byte subIndex)
    {
        keyPrefix[31] = subIndex;
        byte[]? result = RustVerkleLib.VerkleTrieGet(_verkleTrieObj, keyPrefix);
        return result;
    }
    
    public Span<byte> GetValueSpan(byte[] keyPrefix, byte subIndex)
    {
        keyPrefix[31] = subIndex;
        return RustVerkleLib.VerkleTrieGetSpan(_verkleTrieObj, keyPrefix);
    }

    private byte[] GetTreeKey(Address address, UInt256 treeIndex, byte subIndexBytes)
    {
        byte[] treeKeyPrefix = GetTreeKeyPrefix(address, treeIndex);
        treeKeyPrefix[31] = subIndexBytes;
        return treeKeyPrefix;
    }

    public byte[][] GetTreeKeysForAccount(Address address)
    {
        byte[] treeKeyPrefix = GetTreeKeyPrefix(address, 0);

        byte[] treeKeyVersion = new byte[32];
        Buffer.BlockCopy(treeKeyPrefix, 0, treeKeyVersion, 0, 31);
        treeKeyVersion[31] = VersionLeafKey;

        byte[] treeKeyBalance = new byte[32];
        Buffer.BlockCopy(treeKeyPrefix, 0, treeKeyBalance, 0, 31);
        treeKeyBalance[31] = BalanceLeafKey;

        byte[] treeKeyNounce = new byte[32];
        Buffer.BlockCopy(treeKeyPrefix, 0, treeKeyNounce, 0, 31);
        treeKeyNounce[31] = NonceLeafKey;

        byte[] treeKeyCodeKeccak = new byte[32];
        Buffer.BlockCopy(treeKeyPrefix, 0, treeKeyCodeKeccak, 0, 31);
        treeKeyCodeKeccak[31] = CodeKeccakLeafKey;

        byte[] treeKeyCodeSize = new byte[32];
        Buffer.BlockCopy(treeKeyPrefix, 0, treeKeyCodeSize, 0, 31);
        treeKeyCodeSize[31] = CodeSizeLeafKey;

        return new[] { treeKeyVersion, treeKeyBalance, treeKeyNounce, treeKeyCodeKeccak, treeKeyCodeSize };
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
        return GetTreeKey(address, treeIndex, subIndex.ToBigEndian()[31]);
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
        return GetTreeKey(address, treeIndex, subIndex.ToBigEndian()[31]);
    }
        
    public void SetCode(Address address, byte[] code)
    {
        byte[][] chunkifiedCode = ChunkifyCode(code);
        byte[] chunkKey;
        for (int i = 0; i < chunkifiedCode.Length; i++)
        {
            chunkKey = GetTreeKeyForCodeChunk(address, (UInt256)i);
            SetValue(chunkKey, chunkifiedCode[i]);
        }
    }
    private byte[][] ChunkifyCode(byte[] code)
    {
        const int PUSH_OFFSET = 95;
        const int PUSH1 = PUSH_OFFSET + 1;
        const int PUSH32 = PUSH_OFFSET + 32;
            
        // To ensure that the code can be split into chunks of 31 bytes
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
                try
                {
                    bytesToExecData[pos + i] = pushLength - i;
                }
                catch (IndexOutOfRangeException e)
                {
                    Console.WriteLine(e);
                    break;
                }
                
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
    
    public void UpdateRootHash()
    {
        byte[] rootHash = RustVerkleLib.VerkleTrieGetStateRoot(_verkleTrieObj);
        RootHash = new Keccak(rootHash);
    }
    
    public void Accept(ITreeVisitor visitor, Keccak rootHash, VisitingOptions visitingOptions = VisitingOptions.ExpectAccounts)
    {
        if (visitor is null) throw new ArgumentNullException(nameof(visitor));
        if (rootHash is null) throw new ArgumentNullException(nameof(rootHash));
        throw new InvalidOperationException("No support for visiting a VerkleTree");
    }
    
    public void Commit(long blockNumber)
    {
        if (_currentCommit is null)
        {
            throw new InvalidAsynchronousStateException(
                $"{nameof(_currentCommit)} is NULL when calling {nameof(Commit)}");
        }
            
        if (!_allowCommits)
        {
            throw new TrieException("Commits are not allowed on this trie.");
        }
    }

}
