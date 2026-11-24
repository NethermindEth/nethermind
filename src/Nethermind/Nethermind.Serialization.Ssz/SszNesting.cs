// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Ssz;

/// <summary>Tracks which nested writers are open and how much each has written, so that writers themselves are immutable.</summary>
internal sealed class SszNesting
{
    /// <summary>The open writers from the outermost, after a sentinel that stands for no writer.</summary>
    private (int Id, int Written)[] _open = new (int, int)[8];
    private int _lastId;

    /// <summary>The number of open writers.</summary>
    public int Depth { get; private set; }

    /// <summary>The number of members or items that the innermost writer has written.</summary>
    public int Written => _open[Depth].Written;

    /// <returns>The id of the opened writer.</returns>
    public int Open()
    {
        if (++Depth == _open.Length) Array.Resize(ref _open, Depth * 2);
        _open[Depth] = (++_lastId, 0);
        return _lastId;
    }

    public void BeginMember(int id, int index, string name)
    {
        EnsureInnermost(id, name);
        if (_open[Depth].Written != index) ThrowOutOfOrder(name);
        _open[Depth].Written++;
    }

    /// <returns>The number of items that the writer <paramref name="id"/> has written, including these.</returns>
    public int AddItems(int id, int count)
    {
        EnsureInnermost(id, "an item");
        return _open[Depth].Written = checked(_open[Depth].Written + count);
    }

    /// <summary>Closes the writer <paramref name="id"/> if it has written <paramref name="count"/> members.</summary>
    public bool Close(int id, int count) => _open[Depth] == (id, count) && Close(id);

    /// <summary>Closes the writer <paramref name="id"/>, however many items it has written.</summary>
    public bool Close(int id)
    {
        if (_open[Depth].Id != id) return false;
        Depth--;
        return true;
    }

    public void EnsureInnermost(int id, string name)
    {
        if (_open[Depth].Id != id) ThrowNotInnermost(name);
    }

    public void Reset() => Depth = 0;

    private static void ThrowNotInnermost(string name) =>
        throw new InvalidOperationException($"SSZ writer: {name} cannot be accessed while a nested writer is open or incomplete, or after its own writer is closed.");

    private static void ThrowOutOfOrder(string name) =>
        throw new InvalidOperationException($"SSZ writer: {name} is out of order. Every member is written once, first the fixed-size ones and then the variable-size ones, each in SSZ order.");
}
