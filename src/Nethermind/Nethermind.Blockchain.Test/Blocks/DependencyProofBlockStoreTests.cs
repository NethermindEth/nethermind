// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Blocks;

[Parallelizable(ParallelScope.All)]
public class DependencyProofBlockStoreTests
{
    [Test]
    public void Stores_the_proof_apart_and_gives_it_back_to_a_block_read_without_it()
    {
        byte[] proof = [0x01, 0xaa, 0xbb];
        DependencyProofStore proofs = new(new TestMemDb());
        DependencyProofBlockStore store = new(new BlockStore(new TestMemDb()), proofs);
        Block block = Build.A.Block.WithNumber(1).TestObject;
        block.Header.DependencyProofHash = Keccak.Compute(proof);
        block.Header.Hash = block.Header.CalculateHash();
        block.DependencyProof = proof;

        store.Insert(block);
        Block readBack = store.Get(block.Number, block.Hash!, RlpBehaviors.None, shouldCache: false)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proofs.Get(block.Number, block.Hash!), Is.EqualTo(proof));
            Assert.That(readBack.DependencyProof, Is.Null);
            Assert.That(proofs.WithProof(readBack).DependencyProof, Is.EqualTo(proof));
        }

        store.Delete(block.Number, block.Hash!);
        Assert.That(proofs.Get(block.Number, block.Hash!), Is.Null);
    }
}
