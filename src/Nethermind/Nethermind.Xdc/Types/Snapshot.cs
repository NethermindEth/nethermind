// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Clique;
using Nethermind.Xdc.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Xdc.Types
{
    public class Snapshot : ICloneable
    {
        public long Number { get; set; }
        public Hash256 Hash { get; set; }
        public Address[] NextEpochCandidates { get; set; }

        internal Snapshot(long number, Hash256 hash, Address[] nextEpochCandidates)
        {
            Number = number;
            Hash = hash;
            NextEpochCandidates = nextEpochCandidates;
        }

        public object Clone() =>
            new Snapshot(Number,
                Hash,
                [.. NextEpochCandidates]);
    }
}
