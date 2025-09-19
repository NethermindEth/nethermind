// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Xdc.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Xdc.Types;

public class Snapshot(long number, Hash256 hash, Address[] nextEpochCandidates) : ICloneable
{
    public long Number { get; set; } = number;
    public Hash256 Hash { get; set; } = hash;
    public Address[] NextEpochCandidates { get; set; } = nextEpochCandidates;

    public object Clone() =>
        new Snapshot(Number,
            Hash,
            [.. NextEpochCandidates]);
    public override bool Equals(object? obj)
    {
        if (obj is not Snapshot other)
        {
            return false;
        }
        if (Number != other.Number || Hash != other.Hash || NextEpochCandidates.Length != other.NextEpochCandidates.Length)
        {
            return false;
        }
        for (int i = 0; i < NextEpochCandidates.Length; i++)
        {
            if (NextEpochCandidates[i] != other.NextEpochCandidates[i])
            {
                return false;
            }
        }
        return true;

    }
    public override string ToString() => $"{Number}:{Hash.ToShortString()}:{String.Join<Address>(',', NextEpochCandidates)}";

    public override int GetHashCode() => base.GetHashCode();
}
