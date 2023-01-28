// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Core2.Types
{
    public struct DomainType : IEquatable<DomainType>
    {
        public const int Length = sizeof(uint);

        private readonly uint _number;

        public ReadOnlySpan<byte> AsSpan()
        {
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
        }

        public DomainType(Span<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length,
                    $"{nameof(DomainType)} must have exactly {Length} bytes");
            }
            _number = MemoryMarshal.Cast<byte, uint>(span)[0];
        }

        public static bool operator ==(DomainType a, DomainType b)
        {
            return a._number == b._number;
        }

        public static bool operator !=(DomainType a, DomainType b)
        {
            return !(a == b);
        }

        public bool Equals(DomainType other)
        {
            return _number == other._number;
        }

        public override bool Equals(object? obj)
        {
            return obj is DomainType other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _number.GetHashCode();
        }

        public override string ToString()
        {
            return AsSpan().ToHexString(true);
        }
    }
}
