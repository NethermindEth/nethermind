// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Trie.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.Store.Test.NodeBuffer;

public class KeccakBatchTests
{
    [Test]
    public void Hashes_match_scalar([Values(1, 2, 3, 4, 7, 8, 9, 17, 49)] int count)
    {
        // One length either side of each rate-block boundary, up to the largest node RLP, covers every padded class.
        int[] lengths = [1, 135, 136, 271, 272, 532];
        (int Start, int Length)[] messages = new (int, int)[count];
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            messages[i] = (total, lengths[i % lengths.Length]);
            total += messages[i].Length;
        }

        byte[] buffer = new byte[total];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)(i * 31 + 7);

        ValueHash256[] hashes = new ValueHash256[count];
        KeccakBatch.Hash(buffer, messages, hashes);

        Assert.That(hashes, Is.EqualTo(messages.Select(message => ValueKeccak.Compute(buffer.AsSpan(message.Start, message.Length))).ToArray()));
    }
}
