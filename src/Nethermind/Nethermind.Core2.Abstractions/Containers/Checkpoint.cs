// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    [DebuggerDisplay("{Epoch}_{Root}")]
    public struct Checkpoint : IEquatable<Checkpoint>
    {
        public static readonly Checkpoint Zero = new Checkpoint(Epoch.Zero, Root.Zero);

        public Checkpoint(Epoch epoch, Root root)
        {
            Epoch = epoch;
            Root = root;
        }

        public Epoch Epoch { get; }

        public Root Root { get; private set; }

        public static Checkpoint Clone(Checkpoint other)
        {
            var clone = new Checkpoint(
                other.Epoch,
                other.Root);
            return clone;
        }

        public override bool Equals(object? obj)
        {
            return obj is Checkpoint other && Equals(other);
        }

        public bool Equals(Checkpoint other)
        {
            return Epoch == other.Epoch
                   && Root.Equals(other.Root);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Epoch, Root);
        }

        public static bool operator ==(Checkpoint a, Checkpoint b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(Checkpoint a, Checkpoint b)
        {
            return !(a == b);
        }

        public override string ToString()
        {
            return $"{Epoch}_{Root.ToString().Substring(0, 10)}";
        }
    }
}
