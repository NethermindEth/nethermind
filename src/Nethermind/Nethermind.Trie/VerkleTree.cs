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
        MainStorageOffsetBase.LeftShift(MainStorageOffsetExponent, out MainStorageOffset);
        
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

    public byte[] GetTreeKeyPrefix(Address address, UInt256 treeIndex)
    {
        // allocate the array on stack  
        Span<byte> keyPrefix = stackalloc byte[64];
        // first 12 bytes are '0' padding to convert 12 byte address -> 32 bytes
        Span<byte> cursor = keyPrefix.Slice(12);
        address.Bytes.CopyTo(cursor);
        // copy the address to the remaining 20 bytes
        cursor = cursor.Slice(20);
        // copy the tree index to the remaining 32 bytes
        treeIndex.ToBigEndian(cursor);
        byte[] prefix = RustVerkleLib.CalculatePedersenHash(keyPrefix);
        prefix[31] = 0;
        return prefix;
    }
    
    public byte[] GetTreeKeyPrefixAccount(Address address) => GetTreeKeyPrefix(address, 0);
    
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
        byte[] treeKeyPrefix = GetTreeKeyPrefix(address, treeIndex);
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

    private byte[] GetTreeKeyForVersion(Address address) => GetTreeKey(address, UInt256.Zero, VersionLeafKey);
    private byte[] GetTreeKeyForBalance(Address address) => GetTreeKey(address, UInt256.Zero, BalanceLeafKey);
    private byte[] GetTreeKeyForNonce(Address address) => GetTreeKey(address, UInt256.Zero, NonceLeafKey);
    private byte[] GetTreeKeyForCodeKeccak(Address address) => GetTreeKey(address, UInt256.Zero, CodeKeccakLeafKey);
    private byte[] GetTreeKeyForCodeSize(Address address) => GetTreeKey(address, UInt256.Zero, CodeSizeLeafKey);
    
    public byte[] GetTreeKeyForCodeChunk(Address address, UInt256 chunk)
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
        CodeChunkEnumerator chunkEnumerator = new(code);
        while (chunkEnumerator.TryGetNextChunk(out byte[] chunk))
        {
            // byte[] chunkKey = GetTreeKeyForCodeChunk(address, (UInt256)i);
            
            // find tree index and the sub index in verkle tree for the code chunk
            FillTreeAndSubIndexForChunk(i, ref subIndexBytes, out UInt256 treeIndex);
            
            byte[] chunkKey = GetTreeKeyPrefix(address, treeIndex);
            chunkKey[31] = subIndexBytes[31];
            SetValue(chunkKey, chunk);
            i++;
        }
    }

    private void FillTreeAndSubIndexForChunk(UInt256 chunkId, ref Span<byte> subIndexBytes, out UInt256 treeIndex)
    {
        UInt256 chunkOffset = CodeOffset + chunkId;
        treeIndex = chunkOffset / VerkleNodeWidth;
        UInt256.Mod(chunkOffset, VerkleNodeWidth, out UInt256 subIndex);
        subIndex.ToBigEndian(subIndexBytes);
    }

    private ref struct CodeChunkEnumerator
    {
        const byte PushOffset = 95;
        const byte Push1 = PushOffset + 1;
        const byte Push32 = PushOffset + 32;
        
        private Span<byte> _code;
        private byte _rollingOverPushLength = 0;
        private readonly byte[] _bufferChunk = new byte[32];
        private readonly Span<byte> _bufferChunkCodePart;

        public CodeChunkEnumerator(Span<byte> code)
        {
            _code = code;
            _bufferChunkCodePart = _bufferChunk.AsSpan().Slice(1);
        }

        // Try get next chunk
        public bool TryGetNextChunk(out byte[] chunk)
        {
            chunk = _bufferChunk;
            
            // we don't have chunks left
            if (_code.IsEmpty)
            {
                return false;
            }

            // we don't have full chunk
            if (_code.Length < 31)
            {
                // need to have trailing zeroes
                _bufferChunkCodePart.Fill(0);
                
                // set number of push bytes
                _bufferChunk[0] = _rollingOverPushLength;
                
                // copy main bytes
                _code.CopyTo(_bufferChunkCodePart);
                
                // we are done
                _code = Span<byte>.Empty;
            }
            else
            {
                // fill up chunk to store
                
                // get current chunk of code
                Span<byte> currentChunk = _code.Slice(0, 31);

                // copy main bytes
                currentChunk.CopyTo(_bufferChunkCodePart);

                switch (_rollingOverPushLength)
                {
                    case 32 or 31: // all bytes are roll over
                        
                        // set number of push bytes
                        _bufferChunk[0] = 31;
                        
                        // if 32, then we will roll over with 1 to even next chunk
                        _rollingOverPushLength -= 31;
                        break;
                    default:
                        // set number of push bytes
                        _bufferChunk[0] = _rollingOverPushLength;
                        _rollingOverPushLength = 0;

                        // check if we have a push instruction in remaining code
                        // ignore the bytes we rolled over, they are not instructions
                        for (int i =  _bufferChunk[0]; i < 31;)
                        {
                            byte instruction = currentChunk[i];
                            i++;
                            if (instruction is >= Push1 and <= Push32)
                            {
                                // we calculate data to ignore in code
                                i += instruction - PushOffset;

                                // check if we rolled over the chunk
                                _rollingOverPushLength = (byte)Math.Max(i - 31, 0);
                            }
                        }

                        break;
                }
                
                // move to next chunk
                _code = _code.Slice(31);
            }

            return true;
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

}
