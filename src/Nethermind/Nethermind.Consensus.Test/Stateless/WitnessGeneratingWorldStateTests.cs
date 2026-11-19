// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class WitnessGeneratingWorldStateTests
{
    [Test]
    public void Repeated_access_reads_each_code_hash_once()
    {
        CountingCodeState state = new();
        (WitnessGeneratingWorldState witnessState, BlockHeader parent) = CreateWitnessState(state);
        byte[] firstCode = [0x00];
        byte[] secondCode = [0x5b, 0x00];

        state.SetCode(firstCode);
        witnessState.RecordBytecodeAccess(TestItem.AddressA);
        witnessState.RecordBytecodeAccess(TestItem.AddressA);
        witnessState.RecordBytecodeAccess(TestItem.AddressB);
        state.SetCode(secondCode);
        witnessState.RecordBytecodeAccess(TestItem.AddressA);
        witnessState.RecordBytecodeAccess(TestItem.AddressA);

        using Witness witness = witnessState.GetWitness(parent);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Reads, Is.EqualTo(2));
            Assert.That(witness.Codes, Is.EquivalentTo(new[] { firstCode, secondCode }));
            Assert.That(witness.Keys, Is.EquivalentTo(new[] { TestItem.AddressA.Bytes.ToArray(), TestItem.AddressB.Bytes.ToArray() }));
        }
    }

    [Test]
    public void Code_deployed_during_execution_is_omitted_from_the_witness([Values] bool transient)
    {
        CountingCodeState state = new();
        (WitnessGeneratingWorldState witnessState, BlockHeader parent) = CreateWitnessState(state);
        byte[] code = [0x00];
        ValueHash256 hash = ValueKeccak.Compute(code);
        if (transient)
        {
            witnessState.InsertTransientCode(TestItem.AddressA, hash, code, Frontier.Instance);
        }
        else
        {
            witnessState.InsertCode(TestItem.AddressA, hash, code, Frontier.Instance);
        }

        witnessState.RecordBytecodeAccess(TestItem.AddressA);

        using Witness witness = witnessState.GetWitness(parent);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Reads, Is.Zero);
            Assert.That(witness.Codes, Is.Empty);
        }
    }

    private static (WitnessGeneratingWorldState State, BlockHeader Parent) CreateWitnessState(CountingCodeState state)
    {
        BlockHeader parent = Build.A.BlockHeader.WithStateRoot(Keccak.EmptyTreeHash).TestObject;
        IHeaderFinder finder = Substitute.For<IHeaderFinder>();
        finder.Get(parent.Hash!).Returns(parent);
        IReadOnlyTrieStore trieStore = Substitute.For<IReadOnlyTrieStore>();
        trieStore.GetTrieStore(null).Returns(NullTrieStore.Instance);
        return (new WitnessGeneratingWorldState(state, Substitute.For<IStateReader>(), trieStore, new WitnessHeaderRecorder(), finder), parent);
    }

    private sealed class CountingCodeState() : WorldStateDecorator(Substitute.For<IWorldState>())
    {
        private ValueHash256 _codeHash;
        private ReadOnlyMemory<byte> _code;
        public int Reads { get; private set; }

        public void SetCode(ReadOnlyMemory<byte> code)
        {
            _code = code;
            _codeHash = ValueKeccak.Compute(code.Span);
        }

        public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        {
            SetCode(code);
            return true;
        }

        public override void InsertTransientCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec) => SetCode(code);

        public override ref readonly ValueHash256 GetCodeHash(Address address) => ref _codeHash;

        public override ReadOnlyMemory<byte> GetCode(Address address)
        {
            Reads++;
            return _code;
        }
    }
}
