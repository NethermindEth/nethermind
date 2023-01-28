// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Core2.Types
{
    public struct ForkVersion : IEquatable<ForkVersion>
    {
        public const int Length = sizeof(uint);
        public static ForkVersion Zero = new ForkVersion();

        private readonly uint _number;

        public ForkVersion(ReadOnlySpan<byte> span)
        {
            if (span.Length != sizeof(uint))
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length,
                    $"{nameof(ForkVersion)} must have exactly {sizeof(uint)} bytes");
            }

            _number = MemoryMarshal.Cast<byte, uint>(span)[0];
        }

        public ReadOnlySpan<byte> AsSpan()
        {
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
        }

        public bool Equals(ForkVersion other)
        {
            return _number == other._number;
        }

        public override bool Equals(object? obj)
        {
            return obj is ForkVersion other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _number.GetHashCode();
        }

        public static bool operator ==(ForkVersion a, ForkVersion b)
        {
            return a._number == b._number;
        }

        public static bool operator !=(ForkVersion a, ForkVersion b)
        {
            return !(a == b);
        }

        public override string ToString()
        {
            return AsSpan().ToHexString(true);
        }
    }
}
