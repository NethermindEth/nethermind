// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace MainApp.Samples;

// Just a couple of classes from https://github.com/flcl42/nethermind/tree/master/src/Nethermind/Nethermind.Core2.Abstractions

public class BeaconBlockHeader : IEquatable<BeaconBlockHeader>
{
    public static readonly BeaconBlockHeader Zero =
        new BeaconBlockHeader(Slot.Zero, Root.Zero, Root.Zero, Root.Zero);

    public BeaconBlockHeader(
        Slot slot,
        Root parentRoot,
        Root stateRoot,
        Root bodyRoot)
    {
        Slot = slot;
        ParentRoot = parentRoot;
        StateRoot = stateRoot;
        BodyRoot = bodyRoot;
    }

    public BeaconBlockHeader(Root bodyRoot)
        : this(Slot.Zero, Root.Zero, Root.Zero, bodyRoot)
    {
    }

    public Root BodyRoot { get; private set; }
    public Root ParentRoot { get; private set; }
    public Slot Slot { get; private set; }
    public Root StateRoot { get; private set; }

    /// <summary>
    /// Creates a deep copy of the object.
    /// </summary>
    public static BeaconBlockHeader Clone(BeaconBlockHeader other)
    {
        var clone = new BeaconBlockHeader(other.BodyRoot)
        {
            Slot = other.Slot,
            ParentRoot = other.ParentRoot,
            StateRoot = other.StateRoot,
            BodyRoot = other.BodyRoot
        };
        return clone;
    }

    public void SetStateRoot(Root stateRoot)
    {
        StateRoot = stateRoot;
    }

    public bool Equals(BeaconBlockHeader? other)
    {
        if (ReferenceEquals(other, null))
        {
            return false;
        }

        if (ReferenceEquals(other, this))
        {
            return true;
        }

        return BodyRoot.Equals(other.BodyRoot)
            && ParentRoot.Equals(other.ParentRoot)
            && Slot == other.Slot
            && StateRoot.Equals(other.StateRoot);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(BodyRoot, ParentRoot, Slot, StateRoot);
    }

    public override bool Equals(object? obj)
    {
        var other = obj as BeaconBlockHeader;
        return !(other is null) && Equals(other);
    }

    public override string ToString()
    {
        return $"s={Slot}_p={ParentRoot.ToString().Substring(0, 10)}_st={StateRoot.ToString().Substring(0, 10)}_bd={BodyRoot.ToString().Substring(0, 10)}";
    }
}

[DebuggerDisplay("{Number}")]
public struct Slot : IEquatable<Slot>, IComparable<Slot>
{
    private ulong _number;

    public Slot(ulong number)
    {
        _number = number;
    }

    public static Slot? None => default;

    public static Slot Zero => new Slot(0);

    public static Slot One => new Slot(1);

    public ulong Number => _number;

    public static bool operator <(Slot a, Slot b)
    {
        return a._number < b._number;
    }

    public static bool operator >(Slot a, Slot b)
    {
        return a._number > b._number;
    }

    public static bool operator <=(Slot a, Slot b)
    {
        return a._number <= b._number;
    }

    public static bool operator >=(Slot a, Slot b)
    {
        return a._number >= b._number;
    }

    public static bool operator ==(Slot a, Slot b)
    {
        return a._number == b._number;
    }

    public static bool operator !=(Slot a, Slot b)
    {
        return !(a == b);
    }

    public static Slot operator -(Slot left, Slot right)
    {
        return new Slot(left._number - right._number);
    }

    public static Slot operator %(Slot left, Slot right)
    {
        return new Slot(left._number % right._number);
    }

    public static Slot operator *(Slot left, ulong right)
    {
        return new Slot(left._number * right);
    }

    public static ulong operator /(Slot left, Slot right)
    {
        return left._number / right._number;
    }

    public static Slot operator +(Slot left, Slot right)
    {
        return new Slot(left._number + right._number);
    }

    public bool Equals(Slot other)
    {
        return _number == other._number;
    }

    public override bool Equals(object? obj)
    {
        return obj is Slot other && Equals(other);
    }

    public override int GetHashCode()
    {
        return _number.GetHashCode();
    }

    public static explicit operator Slot(ulong value)
    {
        return new Slot(value);
    }

    public static explicit operator Slot(int value)
    {
        if (value < 0)
        {
            throw new ArgumentException("Slot number must be > 0", nameof(value));
        }

        return new Slot((ulong)value);
    }

    public static implicit operator ulong(Slot slot)
    {
        return slot._number;
    }

    public static explicit operator int(Slot slot)
    {
        return (int)slot._number;
    }

    public static Slot Max(Slot val1, Slot val2)
    {
        return val1 >= val2 ? val1 : val2;
    }

    public static Slot Min(Slot val1, Slot val2)
    {
        return val1 <= val2 ? val1 : val2;
    }

    public override string ToString()
    {
        return _number.ToString();
    }

    public int CompareTo(Slot other)
    {
        return _number.CompareTo(other._number);
    }

    public static Slot InterlockedCompareExchange(ref Slot location1, Slot value, Slot comparand)
    {
        // Interlocked doesn't support ulong yet (planned for .NET 5), but isomorphic with Int64
        ref long longRef = ref Unsafe.As<ulong, long>(ref location1._number);
        long originalNumber = Interlocked.CompareExchange(ref longRef, (long)value._number, (long)comparand._number);
        return new Slot((ulong)originalNumber);
    }
}
