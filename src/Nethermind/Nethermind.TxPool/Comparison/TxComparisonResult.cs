// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TxPool.Comparison;

public readonly struct TxComparisonResult
{
    public const int NotDecided = 0;
    public const int KeepOld = 1;
    public const int TakeNew = -1;
}
