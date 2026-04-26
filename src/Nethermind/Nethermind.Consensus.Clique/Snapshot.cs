// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Clique
{
    public class Snapshot : ICloneable
    {
        public long Number { get; set; }
        public Hash256 Hash { get; set; }
        public SortedList<Address, long> Signers { get; }
        public List<Vote> Votes { get; init; }
        public Dictionary<Address, Tally> Tally { get; }

        internal Snapshot(long number, Hash256 hash, SortedList<Address, long> signers, Dictionary<Address, Tally> tally)
        {
            Number = number;
            Hash = hash;
            Signers = new SortedList<Address, long>(signers, AddressComparer.Instance);
            Votes = new List<Vote>();
            Tally = tally;
        }

        internal Snapshot(long number, Hash256 hash, SortedList<Address, long> signers)
            : this(number, hash, signers, new Dictionary<Address, Tally>())
        {
        }

        public object Clone() =>
            new Snapshot(Number,
                Hash,
                new SortedList<Address, long>(Signers, AddressComparer.Instance),
                new Dictionary<Address, Tally>(Tally))
            {
                Votes = [.. Votes]
            };

        public long SignerLimit => Signers.Count / 2 + 1;
    }
}
