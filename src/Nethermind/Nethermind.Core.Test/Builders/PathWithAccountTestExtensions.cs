// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;

namespace Nethermind.Core.Test.Builders;

public static class PathWithAccountTestExtensions
{
    /// <summary>Encodes <paramref name="accounts"/> the way a SNAP server does, through <see cref="AccountCollector"/>.</summary>
    public static IPathWithAccountList ToPathWithAccountList(this IEnumerable<PathWithAccount> accounts)
    {
        using AccountCollector collector = new();
        foreach (PathWithAccount account in accounts)
            collector.Collect(account.Path, account.Account is null ? ReadOnlySpan<byte>.Empty : AccountDecoder.Instance.Encode(account.Account));
        return collector.ToPathWithAccountList();
    }

    public static PathWithAccount[] ToArray(this IPathWithAccountList list)
    {
        PathWithAccount[] accounts = new PathWithAccount[list.Count];
        for (int i = 0; i < accounts.Length; i++)
        {
            RefPathWithAccount entry = list[i];
            accounts[i] = new PathWithAccount(entry.Path, entry.Account.ToAccount());
        }
        return accounts;
    }
}
