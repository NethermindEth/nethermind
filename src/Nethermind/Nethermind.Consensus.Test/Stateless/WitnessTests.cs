// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class WitnessTests
{
    [Test]
    public void Decoded_headers_preserve_chain_linkage([Values(1, 2, 256)] int count, [Values] bool breakChain)
    {
        ArrayPoolList<byte[]> encoded = new(count);
        Hash256 parent = Keccak.Zero;
        for (int i = 0; i < count; i++)
        {
            BlockHeader header = Build.A.BlockHeader.WithNumber(i).WithParentHash(breakChain ? Keccak.Zero : parent).TestObject;
            byte[] bytes = Rlp.Encode(header).Bytes;
            encoded.Add(bytes);
            parent = Keccak.Compute(bytes);
        }
        using Witness witness = new()
        {
            Headers = encoded,
            Codes = IOwnedReadOnlyList<byte[]>.Empty,
            State = IOwnedReadOnlyList<byte[]>.Empty,
            Keys = IOwnedReadOnlyList<byte[]>.Empty
        };
        if (breakChain && count > 1)
        {
            Assert.That(() => witness.DecodeHeaders(), Throws.TypeOf<InvalidOperationException>());
        }
        else
        {
            using ArrayPoolList<BlockHeader> decoded = witness.DecodeHeaders();
            Assert.That(decoded.Count, Is.EqualTo(count));
            Assert.That(decoded[^1].Hash, Is.EqualTo(parent));
        }
    }
}
