// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool;

public class TxAnnouncement
{
    public Keccak Hash { get; }
    public int Size { get; }
    public TxType TxType { get; }

    public TxAnnouncement(Transaction tx)
    {
        Hash = tx.Hash!;
        Size = tx.GetLength();
        TxType = tx.Type;
    }
}
