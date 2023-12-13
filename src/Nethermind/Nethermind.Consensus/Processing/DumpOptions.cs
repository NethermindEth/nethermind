// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Processing
{
    [Flags]
    public enum DumpOptions
    {
        None = 0,
        Receipts = 1,
        Parity = 2,
        Geth = 4,
        Rlp = 8,
        RlpLog = 16,
        Default = Receipts | Rlp,
        All = Receipts | Parity | Geth | Rlp
    }
}
