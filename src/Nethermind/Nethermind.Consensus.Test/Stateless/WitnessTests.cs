// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Stateless.Execution.IO;
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

    [Test]
    public void Decoding_rejects_trailing_bytes_after_a_header()
    {
        using Witness witness = new()
        {
            Headers = new ArrayPoolList<byte[]>([[.. Rlp.Encode(Build.A.BlockHeader.TestObject).Bytes, 0x00]]),
            Codes = IOwnedReadOnlyList<byte[]>.Empty,
            State = IOwnedReadOnlyList<byte[]>.Empty,
            Keys = IOwnedReadOnlyList<byte[]>.Empty
        };

        Assert.That(() => witness.DecodeHeaders(), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void Decoded_ssz_witness_keeps_its_state_nodes([Values(0, 1, 3)] int count)
    {
        byte[][] nodes = new byte[count][];
        for (int i = 0; i < count; i++) nodes[i] = [(byte)i, 0xc0, (byte)(i * 7)];
        using Witness original = new()
        {
            Headers = IOwnedReadOnlyList<byte[]>.Empty,
            Codes = IOwnedReadOnlyList<byte[]>.Empty,
            State = new ArrayPoolList<byte[]>(nodes),
            Keys = IOwnedReadOnlyList<byte[]>.Empty
        };
        ExecutionWitness.Decode(ExecutionWitness.Encode(ExecutionWitness.From(original)), out ExecutionWitness decoded);

        Assert.That(Unsafe.SizeOf<SszWitnessState>(), Is.EqualTo(Unsafe.SizeOf<byte[]>()));
        using Witness witness = decoded.ToWitness();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(witness.State.AsSpan().ToArray(), Is.EqualTo(nodes));
            Assert.That(witness.State, Is.EqualTo(nodes));
            Assert.That(witness.State.Count, Is.EqualTo(count));
            for (int i = 0; i < count; i++) Assert.That(witness.State[i], Is.EqualTo(nodes[i]));
        }
    }
}
