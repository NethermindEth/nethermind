// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
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
            collector.Collect(account.Path, account.Account is null ? CappedArray<byte>.Null : new CappedArray<byte>(AccountDecoder.Instance.EncodeAsBytes(account.Account)));
        return collector.ToPathWithAccountList();
    }

    public static PathWithAccount[] ToArray(this IPathWithAccountList list)
    {
        using ArrayPoolList<PathWithAccount> accounts = list.ToPathWithAccounts();
        return accounts.AsSpan().ToArray();
    }
}
