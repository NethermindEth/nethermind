// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public readonly struct PathWithStorageSlot(ValueHash256 keyHash, byte[] slotRlpValue)
        : IEquatable<PathWithStorageSlot>, IEqualityOperators<PathWithStorageSlot, PathWithStorageSlot, bool>
    {
        public ValueHash256 Path { get; } = keyHash;
        public byte[] SlotRlpValue { get; } = slotRlpValue;

        public bool Equals(in PathWithStorageSlot other)
        {
            return Path == other.Path && SlotRlpValue.AsSpan().SequenceEqual(other.SlotRlpValue);
        }

        public bool Equals(PathWithStorageSlot other) => Equals(in other);

        public static bool operator ==(PathWithStorageSlot left, PathWithStorageSlot right) => left.Equals(in right);

        public static bool operator !=(PathWithStorageSlot left, PathWithStorageSlot right) => !left.Equals(in right);

        public override bool Equals(object obj) => obj is PathWithStorageSlot pws && Equals(in pws);

        public override int GetHashCode() => throw new NotImplementedException();
    }
}
