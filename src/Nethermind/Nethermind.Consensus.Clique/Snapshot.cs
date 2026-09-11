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
        public ulong Number { get; set; }
        public Hash256 Hash { get; set; }
        public SortedList<Address, ulong> Signers { get; }
        public List<Vote> Votes { get; init; }
        public Dictionary<Address, Tally> Tally { get; }

        internal Snapshot(ulong number, Hash256 hash, SortedList<Address, ulong> signers, Dictionary<Address, Tally> tally)
        {
            Number = number;
            Hash = hash;
            Signers = new SortedList<Address, ulong>(signers, GenericComparer.GetOptimized<Address>());
            Votes = [];
            Tally = tally;
        }

        internal Snapshot(ulong number, Hash256 hash, SortedList<Address, ulong> signers)
            : this(number, hash, signers, [])
        {
        }

        public object Clone() =>
            new Snapshot(Number,
                Hash,
                new SortedList<Address, ulong>(Signers, GenericComparer.GetOptimized<Address>()),
                new Dictionary<Address, Tally>(Tally))
            {
                Votes = [.. Votes]
            };

        public ulong SignerLimit => (ulong)Signers.Count / 2 + 1;
    }
}
