// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Proofs;

[Parallelizable(ParallelScope.All)]
public class WithdrawalTrieTests
{
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Should_compute_hash_root()
    {
        Block block = Build.A.Block.WithWithdrawals(10).TestObject;
        WithdrawalTrie trie = new(block.Withdrawals!);

        Assert.That(
            trie.RootHash.ToString(), Is.EqualTo("0xf3a83e722a656f6d1813498178b7c9490a7488de8c576144f8bd473c61c3239f"));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Should_verify_proof()
    {
        int count = 10;
        Block block = Build.A.Block.WithWithdrawals(count).TestObject;
        WithdrawalTrie trie = new(block.Withdrawals!, true);

        for (int i = 0; i < count; i++)
        {
            Assert.That(VerifyProof(trie.BuildProof(i), trie.RootHash), Is.True);
        }
    }

    private static bool VerifyProof(byte[][] proof, Hash256 root)
    {
        for (int i = proof.Length - 1; i >= 0; i--)
        {
            byte[] p = proof[i];
            Hash256 hash = Keccak.Compute(p);

            if (i > 0)
            {
                string hex = p.Length < 32 ? p.ToHexString(false) : hash.ToString(false);

                if (!new Rlp(proof[i - 1]).ToString(false).Contains(hex, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else if (hash != root)
            {
                return false;
            }
        }

        return true;
    }
}
