// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Clique;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Db.Blooms;
using NUnit.Framework;

namespace Nethermind.Clique.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class SnapshotDecoderTests
    {
        private readonly Address _signer1 = new("0x7ffc57839b00206d1ad20c69a1981b489f772031");
        private readonly Address _signer2 = new("0xb279182d99e65703f0076e4812653aab85fca0f0");
        private readonly Address _signer3 = new("0x42eb768f2244c8811c63729a21a3569731535f06");

        [Test]
        public void Encodes()
        {
            SnapshotDecoder decoder = new();
            // Prepare snapshot
            Keccak hash = new("0xa33ea6f6c0f1c80a6c7af308a30cb7a7affa4d0d51e6639b739727af0518b50e");
            long number = 3305206L;
            Address candidate = new("0xbe1085bc3e0812f3df63deced87e29b3bc2db524");
            Snapshot expected = GenerateSnapshot(hash, number, candidate);
            // Encode snapshot
            RlpStream stream = new(decoder.GetLength(expected, RlpBehaviors.None));
            decoder.Encode(stream, expected);
            // Decode snapshot
            Snapshot actual = decoder.Decode(stream.Data.AsRlpStream());
            // Validate fields
            Assert.That(actual.Number, Is.EqualTo(expected.Number));
            Assert.That(actual.Hash, Is.EqualTo(expected.Hash));
            Assert.That(actual.Signers, Is.EqualTo(expected.Signers));
            Assert.That(actual.Votes.Count, Is.EqualTo(expected.Votes.Count));
            for (int i = 0; i < expected.Votes.Count; i++)
            {
                Assert.That(actual.Votes[i].Signer, Is.EqualTo(expected.Votes[i].Signer));
                Assert.That(actual.Votes[i].Block, Is.EqualTo(expected.Votes[i].Block));
                Assert.That(actual.Votes[i].Address, Is.EqualTo(expected.Votes[i].Address));
                Assert.That(actual.Votes[i].Authorize, Is.EqualTo(expected.Votes[i].Authorize));
            }
            Assert.That(actual.Tally.Count, Is.EqualTo(expected.Tally.Count));
            foreach (Address address in expected.Tally.Keys)
            {
                Assert.That(actual.Tally[address].Votes, Is.EqualTo(expected.Tally[address].Votes));
                Assert.That(actual.Tally[address].Authorize, Is.EqualTo(expected.Tally[address].Authorize));
            }
        }

        private Snapshot GenerateSnapshot(Keccak hash, long number, Address candidate)
        {
            SortedList<Address, long> signers = new(AddressComparer.Instance);
            signers.Add(_signer1, number - 2);
            signers.Add(_signer2, number - 1);
            signers.Add(_signer3, number - 3);
            List<Vote> votes = new();
            votes.Add(new Vote(_signer1, number - 2, candidate, true));
            votes.Add(new Vote(_signer3, number - 3, candidate, true));
            votes.Add(new Vote(_signer3, number - 6, _signer2, false));
            Dictionary<Address, Tally> tally = new();
            tally[candidate] = new Tally(true);
            tally[candidate].Votes = 2;
            tally[_signer2] = new Tally(false);
            tally[_signer2].Votes = 1;
            Snapshot snapshot = new(number, hash, signers, tally);
            snapshot.Votes = votes;
            return snapshot;
        }
    }
}
