// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Eth;

[Parallelizable(ParallelScope.All)]
public class BlockForRpcTests
{
    [Test]
    public void Size_of_decoded_block_matches_encoded_length()
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithTransactions(
                Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject)
            .TestObject;
        BlockDecoder decoder = new();
        Block decoded = decoder.Decode(decoder.Encode(block).Bytes)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.EncodedSize, Is.Not.Null);
            Assert.That(new BlockForRpc(decoded, false, MainnetSpecProvider.Instance).Size, Is.EqualTo(decoder.GetLength(block, RlpBehaviors.None)));
            Assert.That(new BlockForRpc(block, false, MainnetSpecProvider.Instance).Size, Is.EqualTo(decoded.EncodedSize));
        }
    }
}
