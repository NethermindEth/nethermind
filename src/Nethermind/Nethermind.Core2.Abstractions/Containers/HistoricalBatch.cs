// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Containers
{
    public class HistoricalBatch
    {
        private readonly Root[] _blockRoots;
        private readonly Root[] _stateRoots;

        public HistoricalBatch(Root[] blockRoots, Root[] stateRoots)
        {
            _blockRoots = blockRoots;
            _stateRoots = stateRoots;
        }

        public IReadOnlyList<Root> BlockRoots { get { return _blockRoots; } }

        public IReadOnlyList<Root> StateRoots { get { return _stateRoots; } }

        public bool Equals(HistoricalBatch other)
        {
            return BlockRoots.SequenceEqual(other.BlockRoots)
                   && StateRoots.SequenceEqual(other.StateRoots);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is HistoricalBatch other && Equals(other);
        }

        public override int GetHashCode()
        {
            HashCode hashCode = new HashCode();
            foreach (Root value in BlockRoots)
            {
                hashCode.Add(value);
            }
            foreach (Root value in StateRoots)
            {
                hashCode.Add(value);
            }

            return hashCode.ToHashCode();
        }

    }
}
