// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Merge.Plugin.Test;

internal static class WithdrawalExtensions
{
    public static Withdrawal WithIndex(this Withdrawal withdrawal, uint index)
    {
        withdrawal.Index = index;
        return withdrawal;
    }
}
