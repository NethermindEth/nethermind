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
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public class VerkleTree
{

    protected readonly IVerkleTrieStore _verkleTrieStore;
    protected readonly RustVerkle _verkleTrie;
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
        _verkleTrieStore = new VerkleTrieStore(DatabaseScheme.MemoryDb, logManager);
        _verkleTrie = _verkleTrieStore.CreateTrie(CommitScheme.TestCommitment);
        _logger = logManager?.GetClassLogger<VerkleTree>() ?? throw new ArgumentNullException(nameof(logManager));
        _logger.Info(_verkleTrieStore.ToString());
        _allowCommits = allowCommits;
        RootHash = rootHash;
        
        if (_allowCommits)
        {
            _currentCommit = new ConcurrentQueue<NodeCommitInfo>();
            _commitExceptions = new ConcurrentQueue<Exception>();
        }
    }
    
    public VerkleTree(IVerkleTrieStore verkleTrieStore)
        : this(verkleTrieStore, EmptyTreeHash, true, NullLogManager.Instance)
    {
    }
    public VerkleTree(IVerkleTrieStore verkleTrieStore, ILogManager? logManager)
        : this(verkleTrieStore, EmptyTreeHash, true, logManager)
    {
    }

    public VerkleTree(
        IVerkleTrieStore verkleTrieStore,
        Keccak rootHash,
        bool allowCommits,
        ILogManager? logManager)
    {
        // TODO: do i need to pass roothash here to rust to use for initialization of the library?
        _verkleTrieStore = verkleTrieStore;
        _verkleTrie = _verkleTrieStore.CreateTrie(CommitScheme.TestCommitment);
        _logger = logManager?.GetClassLogger<VerkleTree>() ?? throw new ArgumentNullException(nameof(logManager));
        _logger.Info(_verkleTrieStore.ToString());
        _allowCommits = allowCommits;
        RootHash = rootHash;
        
        if (_allowCommits)
        {
            _currentCommit = new ConcurrentQueue<NodeCommitInfo>();
            _commitExceptions = new ConcurrentQueue<Exception>();
        }
    }


    [DebuggerStepThrough]
    // TODO: add functionality to start with a given root hash (traverse from a starting node)
    // public byte[]? Get(Span<byte> rawKey, Keccak? rootHash = null)
    public byte[]? GetValue(Span<byte> rawKey) => RustVerkleLib.VerkleTrieGet(_verkleTrie, rawKey);
    public byte[]? GetValue(byte[] rawKey) => RustVerkleLib.VerkleTrieGet(_verkleTrie, rawKey);

    public Span<byte> GetValueSpan(byte[] rawKey) => RustVerkleLib.VerkleTrieGetSpan(_verkleTrie, rawKey);
    public Span<byte> GetValueSpan(Span<byte> rawKey) => RustVerkleLib.VerkleTrieGetSpan(_verkleTrie, rawKey);


    [DebuggerStepThrough]
    public void SetValue(byte[] rawKey, byte[] value)
    {
        if (_logger.IsTrace)
            _logger.Trace($"{(value.Length == 0 ? $"Deleting {rawKey.ToHexString()}" : $"Setting {rawKey.ToHexString()} = {value.ToHexString()}")}");
        // TODO; error handling here? or at least a way to check if the operation was successful
        RustVerkleLib.VerkleTrieInsert(_verkleTrie, rawKey, value);
    }
    
    [DebuggerStepThrough]
    public void SetValue(Span<byte> rawKey, Span<byte> value)
    {
        if (_logger.IsTrace)
            _logger.Trace($"{(value.Length == 0 ? $"Deleting {rawKey.ToHexString()}" : $"Setting {rawKey.ToHexString()} = {value.ToHexString()}")}");
        // TODO; error handling here? or at least a way to check if the operation was successful
        RustVerkleLib.VerkleTrieInsert(_verkleTrie, rawKey, value);
    }

    [DebuggerStepThrough]
    public void SetValue(Span<byte> keyPrefix, byte subIndex, byte[] value)
    {
        if (_logger.IsTrace)
            _logger.Trace($"{(value.Length == 0 ? $"Deleting {keyPrefix.ToHexString() + subIndex}" : $"Setting {keyPrefix.ToHexString() + + subIndex} = {value.ToHexString()}")}");
        // TODO; error handling here? or at least a way to check if the operation was successful
        keyPrefix[31] = subIndex;
        RustVerkleLib.VerkleTrieInsert(_verkleTrie, keyPrefix, value);
    }
    
    [DebuggerStepThrough]
    public void SetValue(byte[] keyPrefix, byte subIndex, byte[] value)
    {
        if (_logger.IsTrace)
            _logger.Trace($"{(value.Length == 0 ? $"Deleting {keyPrefix.ToHexString() + subIndex}" : $"Setting {keyPrefix.ToHexString() + + subIndex} = {value.ToHexString()}")}");
        // TODO; error handling here? or at least a way to check if the operation was successful
        keyPrefix[31] = subIndex;
        RustVerkleLib.VerkleTrieInsert(_verkleTrie, keyPrefix, value);
    }
    
    public byte[]? GetValue(byte[] keyPrefix, byte subIndex)
    {
        keyPrefix[31] = subIndex;
        byte[]? result = RustVerkleLib.VerkleTrieGet(_verkleTrie, keyPrefix);
        return result;
    }
    
    public Span<byte> GetValueSpan(byte[] keyPrefix, byte subIndex)
    {
        keyPrefix[31] = subIndex;
        return RustVerkleLib.VerkleTrieGet(_verkleTrie, keyPrefix);
    }

    private byte[] GetTreeKey(Address address, UInt256 treeIndex, byte subIndexBytes)
    {
        byte[] treeKeyPrefix = VerkleUtils.GetTreeKeyPrefix(address, treeIndex);
        treeKeyPrefix[31] = subIndexBytes;
        return treeKeyPrefix;
    }

    // public byte[][] GetTreeKeysForAccount(Address address)
    // {
    //     byte[] treeKeyPrefix = GetTreeKeyPrefix(address, 0);
    //
    //     byte[] treeKeyVersion = new byte[32];
    //     Buffer.BlockCopy(treeKeyPrefix, 0, treeKeyVersion, 0, 31);
    //     treeKeyVersion[31] = VersionLeafKey;
    //
    //     byte[] treeKeyBalance = new byte[32];
    //     Buffer.BlockCopy(treeKeyPrefix, 0, treeKeyBalance, 0, 31);
    //     treeKeyBalance[31] = BalanceLeafKey;
    //
    //     byte[] treeKeyNounce = new byte[32];
    //     Buffer.BlockCopy(treeKeyPrefix, 0, treeKeyNounce, 0, 31);
    //     treeKeyNounce[31] = NonceLeafKey;
    //
    //     byte[] treeKeyCodeKeccak = new byte[32];
    //     Buffer.BlockCopy(treeKeyPrefix, 0, treeKeyCodeKeccak, 0, 31);
    //     treeKeyCodeKeccak[31] = CodeKeccakLeafKey;
    //
    //     byte[] treeKeyCodeSize = new byte[32];
    //     Buffer.BlockCopy(treeKeyPrefix, 0, treeKeyCodeSize, 0, 31);
    //     treeKeyCodeSize[31] = CodeSizeLeafKey;
    //
    //     return new[] { treeKeyVersion, treeKeyBalance, treeKeyNounce, treeKeyCodeKeccak, treeKeyCodeSize };
    // }

    // private byte[] GetTreeKeyForAccountLeaf(Address address, byte leaf)
    // {
    //     return GetTreeKey(address, UInt256.Zero, leaf);
    // }
    
        
    public void SetCode(Address address, byte[] code)
    {
        // Span<byte> processedCode = PrepareCodeForChunkification(code); 
        //
        // int chunkCount = processedCode.Length / 32;
        //
        // for (int i = 0; i < chunkCount; i++)
        // {
        //     byte[] chunkKey = GetTreeKeyForCodeChunk(address, (UInt256)i);
        //     SetValue(chunkKey, processedCode.Slice(i*32, 32));
        // }
        
        UInt256 i = 0;
        Span<byte> subIndexBytes = stackalloc byte[32];
        VerkleUtils.CodeChunkEnumerator chunkEnumerator = new(code);
        while (chunkEnumerator.TryGetNextChunk(out byte[] chunk))
        {
            // byte[] chunkKey = GetTreeKeyForCodeChunk(address, (UInt256)i);
            
            // find tree index and the sub index in verkle tree for the code chunk
            VerkleUtils.FillTreeAndSubIndexForChunk(i, ref subIndexBytes, out UInt256 treeIndex);
            
            byte[] chunkKey = VerkleUtils.GetTreeKeyPrefix(address, treeIndex);
            chunkKey[31] = subIndexBytes[31];
            SetValue(chunkKey, chunk);
            i++;
        }
    }

    

    

    private static Span<byte> PrepareCodeForChunkification(byte[] code)
    {
        const int PUSH_OFFSET = 95;
        const int PUSH1 = PUSH_OFFSET + 1;
        const int PUSH32 = PUSH_OFFSET + 32;
        
        var codeLength = code.Length;
        int[] bytesToExecData  = new int[codeLength];
        int pos = 0;
        int pushLength;
        while (pos < code.Length)
        {
            pushLength = 0;
            if ( PUSH1 <= code[pos] && code[pos] <= PUSH32)
            {
                pushLength = code[pos] - PUSH_OFFSET;
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
        
        int chunkCount = (code.Length + 30) / 31; 
        Span<byte> chunkifyableCode = new byte[chunkCount * 32];
        
        pos = 0;
        Span<byte> codeCursor = chunkifyableCode;    
        for (int i = 0; i < chunkCount; i++)
        {   
            codeCursor[0] = (byte)Math.Min(bytesToExecData[pos], 31);
            codeCursor = codeCursor.Slice(1);
            
            code.Slice(pos, code.Length - pos < 31? code.Length - pos: 31).CopyTo(codeCursor);
            codeCursor = codeCursor.Slice(31);
            pos += 31;
        }

        return chunkifyableCode;
    }

    public void UpdateRootHash()
    {
        byte[] rootHash = RustVerkleLib.VerkleTrieGetStateRoot(_verkleTrie);
        RootHash = new Keccak(rootHash);
    }
    
    public void Accept(ITreeVisitor visitor, Keccak rootHash, VisitingOptions? visitingOptions = null)
    {
        if (visitor is null) throw new ArgumentNullException(nameof(visitor));
        if (rootHash is null) throw new ArgumentNullException(nameof(rootHash));
        visitingOptions ??= VisitingOptions.Default;

        TrieVisitContext trieVisitContext = new()
        {
            // hacky but other solutions are not much better, something nicer would require a bit of thinking
            // we introduced a notion of an account on the visit context level which should have no knowledge of account really
            // but we know that we have multiple optimizations and assumptions on trees
            ExpectAccounts = visitingOptions.ExpectAccounts,
            MaxDegreeOfParallelism = visitingOptions.MaxDegreeOfParallelism
        };

        visitor.VisitTree(rootHash, trieVisitContext);
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
        RustVerkleLib.VerkleTrieFlush(_verkleTrie);
        _verkleTrieStore.FinishBlockCommit(TrieType, blockNumber);
    }

    public byte[] GetVerkleProofForMultipleKeys(byte[,] keys)
    {
        return RustVerkleLib.VerkleProofGetMultiple(_verkleTrie, keys, keys.Length);
    }

    public bool VerifyVerkleProofMultipleKeys(byte[] verkleProof, byte[,] keys, byte[,] values)
    {
        return RustVerkleLib.VerkleProofVerifyMultiple(_verkleTrie, verkleProof, verkleProof.Length, keys, values,
            keys.Length);
    }
}
