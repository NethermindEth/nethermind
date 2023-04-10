// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State
{
    public class StateTreeByPath : PatriciaTree, IStateTree
    {
        private readonly AccountDecoder _decoder = new();

        private static readonly UInt256 CacheSize = 1024;

        private static readonly int CacheSizeInt = (int)CacheSize;

        private static readonly Dictionary<UInt256, byte[]> Cache = new(CacheSizeInt);

        private static readonly Rlp EmptyAccountRlp = Rlp.Encode(Account.TotallyEmpty);

        [DebuggerStepThrough]
        public StateTreeByPath()
            : base(new MemDb(), Keccak.EmptyTreeHash, true, true, NullLogManager.Instance, TrieNodeResolverCapability.Path)
        {
            TrieType = TrieType.State;
        }

        [DebuggerStepThrough]
        public StateTreeByPath(ITrieStore? store, ILogManager? logManager)
            : base(store, Keccak.EmptyTreeHash, true, true, logManager)
        {
            if (store.Capability == TrieNodeResolverCapability.Hash) throw new ArgumentException("Only accepts by path store");
            TrieType = TrieType.State;
        }

        [DebuggerStepThrough]
        public Account? Get(Address address, Keccak? rootHash = null)
        {
            byte[] addressKeyBytes = Keccak.Compute(address.Bytes).Bytes;
            byte[]? bytes = Get(addressKeyBytes, rootHash);
            return bytes is null ? null : _decoder.Decode(bytes.AsRlpStream());
        }

        //[DebuggerStepThrough]
        internal Account? Get(Keccak keccak) // for testing
        {
            byte[]? bytes = Get(keccak.Bytes);
            return bytes is null ? null : _decoder.Decode(bytes.AsRlpStream());
        }

        public void Set(Address address, Account? account)
        {
            ValueKeccak keccak = ValueKeccak.Compute(address.Bytes);
            Set(keccak.BytesAsSpan, account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account));
        }

        [DebuggerStepThrough]
        public Rlp? Set(Keccak keccak, Account? account)
        {
            Rlp rlp = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account);

            Set(keccak.Bytes, rlp);
            return rlp;
        }

        private byte[]? GetStorage(byte[] key, Keccak? rootHash = null)
        {
            // TODO: refactor in the PatriciaTree.Get when supporting random lenght keys in flat storage.
            byte[]? bytes = null;
            if (rootHash is not null && RootHash != rootHash)
            {
                Span<byte> nibbleBytes = stackalloc byte[64 + 64];
                Nibbles.BytesToNibbleBytes(key, nibbleBytes);
                byte[]? nodeBytes = TrieStore.LoadRlp(nibbleBytes, rootHash);
                if (nodeBytes is not null)
                {
                    TrieNode node = new TrieNode(NodeType.Unknown, nodeBytes);
                    node.ResolveNode(TrieStore);
                    bytes = node.Value;
                }
            }

            if (bytes is null && RootHash == rootHash)
            {
                if (RootRef?.IsPersisted == true)
                {
                    byte[]? nodeData = TrieStore[key];
                    if (nodeData is not null)
                    {
                        TrieNode node = new(NodeType.Unknown, nodeData);
                        node.ResolveNode(TrieStore);
                        bytes = node.Value;
                    }
                }
                else
                {
                    bytes = GetInternal(key);
                }
            }

            return bytes;
        }


        public byte[]? GetStorage(in UInt256 index, in Address accountAddress, Keccak? root = null)
        {
            Span<byte> key = stackalloc byte[Capability == TrieNodeResolverCapability.Hash ? 32 : 32 + 32];
            switch (Capability)
            {
                case TrieNodeResolverCapability.Hash:
                    GetStorageKey(index, key);
                    break;
                case TrieNodeResolverCapability.Path:
                    accountAddress.Bytes.CopyTo(key);
                    GetStorageKey(index, key.Slice(32));
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }


            byte[]? value = GetStorage(key.ToArray(), root);


            if (value is null)
            {
                return new byte[] { 0 };
            }

            Rlp.ValueDecoderContext rlp = value.AsRlpValueContext();
            return rlp.DecodeByteArray();
        }

        public void SetStorage(in UInt256 index, byte[] value, in Address accountAddress)
        {
            Span<byte> key = stackalloc byte[Capability == TrieNodeResolverCapability.Hash ? 32 : 32 + 32];
            switch (Capability)
            {
                case TrieNodeResolverCapability.Hash:
                    GetStorageKey(index, key);
                    break;
                case TrieNodeResolverCapability.Path:
                    accountAddress.Bytes.CopyTo(key);
                    GetStorageKey(index, key.Slice(32));
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            SetInternal(key, value);
        }

        public void SetStorage(Keccak key, byte[] value, in Address accountAddress, bool rlpEncode = true)
        {
            throw new ArgumentException("not possible");
        }

        private static void GetStorageKey(in UInt256 index, in Span<byte> key)
        {
            if (index < CacheSize)
            {
                Cache[index].CopyTo(key);
                return;
            }

            index.ToBigEndian(key);

            // in situ calculation
            KeccakHash.ComputeHashBytesToSpan(key, key);
        }

        private void SetInternal(Span<byte> rawKey, byte[] value, bool rlpEncode = true)
        {
            if (value.IsZero())
            {
                Set(rawKey, Array.Empty<byte>());
            }
            else
            {
                Rlp rlpEncoded = rlpEncode ? Rlp.Encode(value) : new Rlp(value);
                Set(rawKey, rlpEncoded);
            }
        }
    }
}
