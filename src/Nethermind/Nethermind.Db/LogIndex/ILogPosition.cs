// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.LogIndex;

public interface ILogPosition<T>: IComparable<T>, IEquatable<T>
    where T:ILogPosition<T>
{
    static abstract int Size { get; }

    int BlockNumber { get; }

    static abstract bool operator <(T p1, T p2);
    static abstract bool operator >(T p1, T p2);
    static abstract bool operator ==(T p1, T p2);
    static abstract bool operator !=(T p1, T p2);

    void WriteFirstTo(Span<byte> dbValue);
    void WriteLastTo(Span<byte> dbValue);

    static abstract T ReadFirstFrom(ReadOnlySpan<byte> dbValue);
    static abstract T ReadLastFrom(ReadOnlySpan<byte> dbValue);

    static abstract T Create(int blockNumber);

    static abstract bool TryParse(string input, out T position);
}
