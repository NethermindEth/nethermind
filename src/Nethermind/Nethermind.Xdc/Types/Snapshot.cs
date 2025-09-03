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
    public class Snapshot(long number, Hash256 hash, Address[] nextEpochCandidates) : ICloneable
    {
        public long Number { get; set; } = number;
        public Hash256 Hash { get; set; } = hash;
        public Address[] NextEpochCandidates { get; set; } = nextEpochCandidates;

        public object Clone() =>
            new Snapshot(Number,
                Hash,
                [.. NextEpochCandidates]);
    }
}
