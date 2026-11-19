// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
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
        WitnessGeneratingWorldState witnessState = new(state, Substitute.For<IStateReader>(),
            Substitute.For<IReadOnlyTrieStore>(), new WitnessHeaderRecorder(), Substitute.For<IHeaderFinder>());

        state.SetCode([0x00]);
        witnessState.RecordBytecodeAccess(TestItem.AddressA);
        witnessState.RecordBytecodeAccess(TestItem.AddressA);
        witnessState.RecordBytecodeAccess(TestItem.AddressB);
        state.SetCode([0x5b, 0x00]);
        witnessState.RecordBytecodeAccess(TestItem.AddressA);
        witnessState.RecordBytecodeAccess(TestItem.AddressA);

        Assert.That(state.Reads, Is.EqualTo(2));
    }

    private sealed class CountingCodeState() : WorldStateDecorator(Substitute.For<IWorldState>())
    {
        private ValueHash256 _codeHash;
        private ReadOnlyMemory<byte> _code;
        public int Reads { get; private set; }

        public void SetCode(byte[] code)
        {
            _code = code;
            _codeHash = ValueKeccak.Compute(code);
        }

        public override ref readonly ValueHash256 GetCodeHash(Address address) => ref _codeHash;

        public override ReadOnlyMemory<byte> GetCode(Address address)
        {
            Reads++;
            return _code;
        }
    }
}
