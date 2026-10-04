// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Diagnostics;

/// <summary>Experiment: whether the block-processing thread is executing transactions, for helpers that throttle meanwhile.</summary>
public static class ExecutionPhase
{
    private static volatile bool s_executing;

    public static bool Executing => s_executing;

    /// <summary>Raised when the transactions of a block are done, so throttled helpers can widen at once.</summary>
    public static event Action? Ended;

    public static void Begin() => s_executing = true;

    public static void End()
    {
        if (!s_executing) return;
        s_executing = false;
        Ended?.Invoke();
    }
}
