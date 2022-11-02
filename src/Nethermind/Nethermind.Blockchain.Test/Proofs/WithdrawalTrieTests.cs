using System;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Proofs;

public class WithdrawalTrieTests
{
    [Test]
    public void Should_compute_hash_root()
    {
        var block = Build.A.Block.WithWithdrawals(10).TestObject;
        var trie = new WithdrawalTrie(block.Withdrawals!);

        Assert.AreEqual(
            "0xf3a83e722a656f6d1813498178b7c9490a7488de8c576144f8bd473c61c3239f",
            trie.RootHash.ToString());
    }

    [Test]
    public void Should_verify_proof()
    {
        var count = 1;
        var block = Build.A.Block.WithWithdrawals(count).TestObject;
        var trie = new WithdrawalTrie(block.Withdrawals!, true);
        
        for (int i = 0; i < count; i++)
        {
            VerifyProof(trie.BuildProof(i), trie.RootHash);
        }
    }

    private static void VerifyProof(byte[][] proof, Keccak root)
    {
        for (int i = proof.Length; i > 0; i--)
        {
            Keccak proofHash = Keccak.Compute(proof[i - 1]);
            if (i > 1)
            {
                if (!new Rlp(proof[i - 2]).ToString(false).Contains(proofHash.ToString(false)))
                {
                    throw new InvalidDataException();
                }
            }
            else if (proofHash != root)
            {
                throw new InvalidDataException();
            }
        }
    }
}
