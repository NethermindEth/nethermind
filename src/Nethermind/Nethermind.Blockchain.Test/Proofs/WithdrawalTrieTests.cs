// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Proofs;

public class WithdrawalTrieTests
{
    [Test, Timeout(Timeout.MaxTestTime)]
    public void Should_compute_hash_root()
    {
        var block = Build.A.Block.WithWithdrawals(10).TestObject;
        var trie = new WithdrawalTrie(block.Withdrawals!);

        Assert.AreEqual(
            "0xf3a83e722a656f6d1813498178b7c9490a7488de8c576144f8bd473c61c3239f",
            trie.RootHash.ToString());
    }

    [Test, Timeout(Timeout.MaxTestTime)]
    public void Should_verify_proof()
    {
        var count = 10;
        var block = Build.A.Block.WithWithdrawals(count).TestObject;
        var trie = new WithdrawalTrie(block.Withdrawals!, true);

        for (int i = 0; i < count; i++)
        {
            Assert.IsTrue(VerifyProof(trie.BuildProof(i), trie.RootHash));
        }
    }

    private static bool VerifyProof(byte[][] proof, Keccak root)
    {
        for (var i = proof.Length - 1; i >= 0; i--)
        {
            var p = proof[i];
            var hash = Keccak.Compute(p);

            if (i > 0)
            {
                var hex = p.Length < 32 ? p.ToHexString(false) : hash.ToString(false);

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
