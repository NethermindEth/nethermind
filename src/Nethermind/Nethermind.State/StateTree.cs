// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State
{
    public class StateTree : IPatriciaTree
    {
        private readonly AccountDecoder _decoder = new();
        private readonly PatriciaTree _patriciaTree;

        private static readonly Rlp EmptyAccountRlp = Rlp.Encode(Account.TotallyEmpty);

        [DebuggerStepThrough]
        public StateTree()
            : this(new TrieStore(new MemDb(), NullLogManager.Instance), NullLogManager.Instance)
        {
        }

        [DebuggerStepThrough]
        public StateTree(ITrieStore? store, ILogManager? logManager)
            : this(new PatriciaTree(store, Keccak.EmptyTreeHash, true, true, logManager) { TrieType = TrieType.State })
        {
        }

        [DebuggerStepThrough]
        public StateTree(PatriciaTree patriciaTree)
        {
            if (patriciaTree.TrieType != TrieType.State) throw new ArgumentException($"{nameof(PatriciaTree.TrieType)} must be {nameof(TrieType.State)}", nameof(patriciaTree));
            _patriciaTree = patriciaTree;
        }

        public Keccak RootHash
        {
            get
            {
                return _patriciaTree.RootHash;
            }
            set
            {
                _patriciaTree.RootHash = value;
            }
        }

        public TrieNode? RootRef
        {
            get
            {
                return _patriciaTree.RootRef;
            }
            set
            {
                _patriciaTree.RootRef = value;
            }
        }

        public ITrieStore TrieStore => _patriciaTree.TrieStore;

        [DebuggerStepThrough]
        public Account? Get(Address address, Keccak? rootHash = null)
        {
            byte[]? bytes = _patriciaTree.Get(ValueKeccak.Compute(address.Bytes).BytesAsSpan, rootHash);
            return bytes is null ? null : _decoder.Decode(bytes.AsRlpStream());
        }

        [DebuggerStepThrough]
        internal Account? Get(Keccak keccak) // for testing
        {
            byte[]? bytes = _patriciaTree.Get(keccak.Bytes);
            return bytes is null ? null : _decoder.Decode(bytes.AsRlpStream());
        }

        public void Set(Address address, Account? account)
        {
            ValueKeccak keccak = ValueKeccak.Compute(address.Bytes);
            _patriciaTree.Set(keccak.BytesAsSpan, account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account));
        }

        [DebuggerStepThrough]
        public Rlp? Set(Keccak keccak, Account? account)
        {
            Rlp rlp = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account);

            _patriciaTree.Set(keccak.Bytes, rlp);
            return rlp;
        }

        public Rlp? Set(in ValueKeccak keccak, Account? account)
        {
            Rlp rlp = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account);
            _patriciaTree.Set(keccak.Bytes, rlp);
            return rlp;
        }

        public void Commit(long blockNumber, bool skipRoot = false, WriteFlags writeFlags = WriteFlags.None)
        {
            _patriciaTree.Commit(blockNumber, skipRoot, writeFlags);
        }

        public void Accept(ITreeVisitor visitor, Keccak rootHash, VisitingOptions? visitingOptions = null)
        {
            _patriciaTree.Accept(visitor, rootHash, visitingOptions);
        }

        public void UpdateRootHash()
        {
            _patriciaTree.UpdateRootHash();
        }

        public void Set(ReadOnlySpan<byte> rawKey, byte[] value)
        {
            _patriciaTree.Set(rawKey, value);
        }

        [DebuggerStepThrough]
        public void Set(ReadOnlySpan<byte> rawKey, Rlp? value)
        {
            Set(rawKey, value is null ? Array.Empty<byte>() : value.Bytes);
        }

        public byte[]? Get(Span<byte> rawKey, Keccak? rootHash = null)
        {
            return _patriciaTree.Get(rawKey, rootHash);
        }
    }
}
