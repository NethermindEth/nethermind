// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History;

/// <summary>A per-pass work budget, checked once per scanned row. <see cref="HistoryWindowPruner"/> uses a real
/// wall-clock budget in production; tests inject a deterministic implementation instead of racing one, per the
/// project's no-timing-tests rule.</summary>
internal interface IPruneBudget
{
    bool Exhausted { get; }
}
