// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Stateless.Execution.IO;
using Nethermind.Trie;
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

    [Test]
    public void Decoded_ssz_witness_serves_its_codes_as_the_memory_they_were_decoded_into()
    {
        byte[][] codes = [[0x60, 0x01, 0x00], [0x5b], []];
        using Witness original = new()
        {
            Headers = IOwnedReadOnlyList<byte[]>.Empty,
            Codes = new ArrayPoolList<byte[]>(codes),
            State = IOwnedReadOnlyList<byte[]>.Empty,
            Keys = IOwnedReadOnlyList<byte[]>.Empty
        };
        ExecutionWitness.Decode(ExecutionWitness.Encode(ExecutionWitness.From(original)), out ExecutionWitness decoded);
        using Witness witness = decoded.ToWitness();
        IReadOnlyList<ReadOnlyMemory<byte>> decodedCodes = (IReadOnlyList<ReadOnlyMemory<byte>>)witness.Codes;

        TrieStoreScopeProvider provider = new(new RawTrieStore(witness.CreateNodeStorage()), witness.CreateCodeDb(), UnavailableStateHeaderProvider.Instance, LimboLogs.Instance);
        Assert.That(provider.TryBeginScope(null, new LocalMetrics(), out IWorldStateScopeProvider.IScope scope), Is.True);
        using IWorldStateScopeProvider.IScope opened = scope;

        byte[] deployed = [0x60, 0x02];
        ValueHash256 deployedHash = ValueKeccak.Compute(deployed);
        using (IWorldStateScopeProvider.ICodeSetter setter = opened.CodeDb.BeginCodeWrite()) setter.Set(deployedHash, deployed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(witness.Codes, Is.EqualTo(codes));
            for (int i = 0; i < codes.Length; i++)
            {
                ReadOnlyMemory<byte> code = opened.CodeDb.GetCode(ValueKeccak.Compute(codes[i]));
                Assert.That(code.ToArray(), Is.EqualTo(codes[i]));
                Assert.That(code.Equals(decodedCodes[i]), Is.True, "served as decoded rather than copied");
            }

            Assert.That(opened.CodeDb.GetCode(deployedHash).ToArray(), Is.EqualTo(deployed));
            Assert.That(opened.CodeDb.GetCode(ValueKeccak.Compute([0xfe])).IsNull(), Is.True);
        }
    }
}
