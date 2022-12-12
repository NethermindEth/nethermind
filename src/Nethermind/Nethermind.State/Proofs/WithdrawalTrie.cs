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
    private static readonly WithdrawalDecoder _codec = new();

    private readonly bool _allowsMerkleProofs;

    public WithdrawalTrie(IEnumerable<Withdrawal> withdrawals, bool allowsProofs = false)
        : base(allowsProofs ? new MemDb() : NullDb.Instance, EmptyTreeHash, false, false, NullLogManager.Instance)
    {
        ArgumentNullException.ThrowIfNull(withdrawals);

        _allowsMerkleProofs = allowsProofs;

        var key = 0;

        foreach (var withdrawal in withdrawals)
        {
            Set(Rlp.Encode(key++).Bytes, _codec.Encode(withdrawal).Bytes);
        }

        UpdateRootHash();
    }

    public byte[][] BuildProof(int index)
    {
        if (!_allowsMerkleProofs)
            throw new InvalidOperationException("Merkle proofs not allowed");

        var proofCollector = new ProofCollector(Rlp.Encode(index).Bytes);

        Accept(proofCollector, RootHash, new() { ExpectAccounts = false });

        return proofCollector.BuildResult();
    }
}
