// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Pbt.Persistence;

/// <summary>A caller-owned, single-pass iterator over persisted PBT data.</summary>
/// <remarks>Dispose the iterator before disposing its reader, including when iteration ends early or throws.</remarks>
public interface IPbtIterator<out T> : IDisposable
{
    /// <summary>Gets the item at the current position after a successful <see cref="MoveNext"/>.</summary>
    T Current { get; }

    /// <summary>Advances to the next item, returning false when the iterator is exhausted.</summary>
    bool MoveNext();
}

internal sealed class PbtIterator<T>(IEnumerator<T> enumerator) : IPbtIterator<T>
{
    public T Current => enumerator.Current;
    public bool MoveNext() => enumerator.MoveNext();
    public void Dispose() => enumerator.Dispose();
}
