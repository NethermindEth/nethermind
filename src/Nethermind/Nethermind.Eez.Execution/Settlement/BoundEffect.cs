// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>A settlement entry bound to the transaction of the settling block that produced its effect.</summary>
public readonly record struct BoundEffect(int EntryIndex, int TransactionIndex, EntryShape Shape, ExecutionEntry Entry, RollupUpdate Update);
