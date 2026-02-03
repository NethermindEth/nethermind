// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TxPool.Comparison;

/// <summary>
/// Named constants for transaction comparison results.
/// </summary>
/// <remarks>
/// In IComparer convention: negative means first arg has higher priority (comes first in sorted order).
/// </remarks>
public readonly struct TxComparisonResult
{
    // TxPool replacement semantics
    /// <summary> Indicates that the new transaction should replace the old one.</summary>
    public const int TakeNew = -1;

    /// <summary> Indicates that the comparison result between two transactions is currently undecided.</summary>
    public const int NotDecided = 0;

    /// <summary> Indicates that the old transaction should be retained instead of the new one. </summary>
    public const int KeepOld = 1;

    // General comparison/sorting semantics (aliases)
    /// <summary>The first argument has higher priority and should come first in sort order.</summary>
    public const int XFirst = TakeNew;
    /// <summary>Transactions are equal in priority.</summary>
    public const int Equal = NotDecided;
    /// <summary>The second argument has higher priority and should come first in sort order.</summary>
    public const int YFirst = KeepOld;
}
