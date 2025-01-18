// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync;

public class AccountCollector : RangeQueryVisitor.ILeafValueCollector
{
    public ArrayPoolList<PathWithAccount> Accounts { get; } = new(0);

    public int Collect(in ValueHash256 path, CappedArray<byte> value)
    {
        if (value.IsNull)
        {
            Accounts.Add(new PathWithAccount(path, null));
            return 32 + 1;
        }

        Account accnt = AccountDecoder.Instance.Decode(new RlpStream(value));
        Accounts.Add(new PathWithAccount(path, accnt));
        return 32 + AccountDecoder.Slim.GetLength(accnt);
    }
}

