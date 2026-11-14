// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.State.SnapServer;

public class AccountCollector : RangeQueryVisitor.ILeafValueCollector
{
    public ArrayPoolList<PathWithAccount> Accounts { get; } = new(0);

    public int Collect(in ValueHash256 path, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            Accounts.Add(new PathWithAccount(path, default));
            return 32 + 1;
        }

        if (!AccountDecoder.Instance.TryDecodeStruct<OffFlag>(value, out AccountStruct accnt))
            throw new TrieException("An account leaf must contain a decodable account value.");
        Accounts.Add(new PathWithAccount(path, accnt));
        return 32 + AccountDecoder.Slim.GetLength(accnt);
    }
}
