// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class TxTypeExtensions
{
    public static bool IsTxTypeWithAccessList(this TxType txType)
    {
        return txType != TxType.Legacy;
    }
}
