// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State
{
    public class StateTree : PatriciaTree
    {
        private readonly AccountDecoder _decoder = new();

        private static readonly Rlp EmptyAccountRlp = Rlp.Encode(Account.TotallyEmpty);

        [DebuggerStepThrough]
        public StateTree(ICappedArrayPool? bufferPool = null)
            : base(new MemDb(), Commitment.EmptyTreeHash, true, true, NullLogManager.Instance, bufferPool: bufferPool)
        {
            TrieType = TrieType.State;
        }

        [DebuggerStepThrough]
        public StateTree(ITrieStore? store, ILogManager? logManager)
            : base(store, Commitment.EmptyTreeHash, true, true, logManager)
        {
            TrieType = TrieType.State;
        }

        [DebuggerStepThrough]
        public Account? Get(Address address, Commitment? rootHash = null)
        {
            byte[]? bytes = Get(ValueCommitment.Compute(address.Bytes).BytesAsSpan, rootHash);
            return bytes is null ? null : _decoder.Decode(bytes.AsRlpStream());
        }

        [DebuggerStepThrough]
        internal Account? Get(Commitment commitment) // for testing
        {
            byte[]? bytes = Get(commitment.Bytes);
            return bytes is null ? null : _decoder.Decode(bytes.AsRlpStream());
        }

        public void Set(Address address, Account? account)
        {
            ValueCommitment commitment = ValueCommitment.Compute(address.Bytes);
            Set(commitment.BytesAsSpan, account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account));
        }

        [DebuggerStepThrough]
        public Rlp? Set(Commitment commitment, Account? account)
        {
            Rlp rlp = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account);

            Set(commitment.Bytes, rlp);
            return rlp;
        }

        public Rlp? Set(in ValueCommitment commitment, Account? account)
        {
            Rlp rlp = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account);

            Set(commitment.Bytes, rlp);
            return rlp;
        }
    }
}
