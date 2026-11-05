// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Snap;

/// <summary>The accounts of a SNAP account range, read in place from their RLP encoding.</summary>
public interface IPathWithAccountList : IRlpWrapper, IDisposable
{
    int Count { get; }
    RefPathWithAccount this[int index] { get; }
}

public sealed class EmptyPathWithAccountList : IPathWithAccountList
{
    public static readonly EmptyPathWithAccountList Instance = new();
    public int Count => 0;
    public RefPathWithAccount this[int index] => throw new IndexOutOfRangeException();
    public int RlpLength => Rlp.LengthOfSequence(0);
    public void Write<TWriter>(ref TWriter writer)
        where TWriter : struct, IRlpWriteBackend, allows ref struct => writer.EncodeNullObject();
    public void Dispose() { }
}

public static class PathWithAccountListExtensions
{
    /// <summary>Decodes every entry of <paramref name="list"/> into a <see cref="PathWithAccount"/>.</summary>
    public static ArrayPoolList<PathWithAccount> ToPathWithAccounts(this IPathWithAccountList list)
    {
        ArrayPoolList<PathWithAccount> accounts = new(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            RefPathWithAccount entry = list[i];
            accounts.Add(new PathWithAccount(entry.Path, entry.Account.ToAccount()));
        }
        return accounts;
    }
}
