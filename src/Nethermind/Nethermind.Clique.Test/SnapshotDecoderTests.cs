// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
            Assert.AreEqual(expected.Number, actual.Number);
            Assert.AreEqual(expected.Hash, actual.Hash);
            Assert.AreEqual(expected.Signers, actual.Signers);
            Assert.AreEqual(expected.Votes.Count, actual.Votes.Count);
            for (int i = 0; i < expected.Votes.Count; i++)
            {
                Assert.AreEqual(expected.Votes[i].Signer, actual.Votes[i].Signer);
                Assert.AreEqual(expected.Votes[i].Block, actual.Votes[i].Block);
                Assert.AreEqual(expected.Votes[i].Address, actual.Votes[i].Address);
                Assert.AreEqual(expected.Votes[i].Authorize, actual.Votes[i].Authorize);
            }
            Assert.AreEqual(expected.Tally.Count, actual.Tally.Count);
            foreach (Address address in expected.Tally.Keys)
            {
                Assert.AreEqual(expected.Tally[address].Votes, actual.Tally[address].Votes);
                Assert.AreEqual(expected.Tally[address].Authorize, actual.Tally[address].Authorize);
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
