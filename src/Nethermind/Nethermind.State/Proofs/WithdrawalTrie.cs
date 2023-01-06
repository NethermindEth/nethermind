// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs;

public class WithdrawalTrie : PatriciaTree
{
    private readonly bool _allowsMerkleProofs;

    public WithdrawalTrie(IEnumerable<IWithdrawal> withdrawals, bool allowsProofs = false)
        : base(allowsProofs ? new MemDb() : NullDb.Instance, EmptyTreeHash, false, false, NullLogManager.Instance)
    {
        ArgumentNullException.ThrowIfNull(withdrawals);

        _allowsMerkleProofs = allowsProofs;

        int key = 0;

        foreach (IWithdrawal? withdrawal in withdrawals)
        {
            Set(Rlp.Encode(key++).Bytes, Rlp.Encode(withdrawal).Bytes);
        }

        UpdateRootHash();
    }

    public byte[][] BuildProof(int index)
    {
        if (!_allowsMerkleProofs) throw new InvalidOperationException("Merkle proofs not allowed");

        ProofCollector? proofCollector = new(Rlp.Encode(index).Bytes);
        Accept(proofCollector, RootHash, new() { ExpectAccounts = false });
        return proofCollector.BuildResult();
    }
}
