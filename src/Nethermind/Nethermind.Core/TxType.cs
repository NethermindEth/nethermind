// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    public enum TxType : byte
    {
        Legacy = 0,
        AccessList = 1,
        EIP1559 = 2,
        Blob = 3,

        DepositTx = 0x7E,
    }
}
