//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
        private readonly Address _signer1 = new Address("0x7ffc57839b00206d1ad20c69a1981b489f772031");
        private readonly Address _signer2 = new Address("0xb279182d99e65703f0076e4812653aab85fca0f0");
        private readonly Address _signer3 = new Address("0x42eb768f2244c8811c63729a21a3569731535f06");

        [Test]
        public void Encodes()
        {
            SnapshotDecoder decoder = new SnapshotDecoder();
            // Prepare snapshot
            Keccak hash = new Keccak("0xa33ea6f6c0f1c80a6c7af308a30cb7a7affa4d0d51e6639b739727af0518b50e");
            long number = 3305206L;
            Address candidate = new Address("0xbe1085bc3e0812f3df63deced87e29b3bc2db524");
            Snapshot expected = GenerateSnapshot(hash, number, candidate);
            // Encode snapshot
            Rlp rlp = decoder.Encode(expected);
            // Decode snapshot
            Snapshot actual = decoder.Decode(rlp.Bytes.AsRlpStream());
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
            SortedList<Address, long> signers = new SortedList<Address, long>(AddressComparer.Instance);
            signers.Add(_signer1, number - 2);
            signers.Add(_signer2, number - 1);
            signers.Add(_signer3, number - 3);
            List<Vote> votes = new List<Vote>();
            votes.Add(new Vote(_signer1, number - 2, candidate, true));
            votes.Add(new Vote(_signer3, number - 3, candidate, true));
            votes.Add(new Vote(_signer3, number - 6, _signer2, false));
            Dictionary<Address, Tally> tally = new Dictionary<Address, Tally>();
            tally[candidate] = new Tally(true);
            tally[candidate].Votes = 2;
            tally[_signer2] = new Tally(false);
            tally[_signer2].Votes = 1;
            Snapshot snapshot = new Snapshot(number, hash, signers, tally);
            snapshot.Votes = votes;
            return snapshot;
        }
    }
}
