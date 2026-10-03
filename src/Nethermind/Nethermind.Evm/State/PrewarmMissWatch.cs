// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.State;

/// <summary>
/// Counts the backing-store storage reads a prewarm run makes on its thread, and calls back once when they reach a
/// threshold, so a run caught in a chain of cold reads can hand its transaction to storage discovery.
/// </summary>
public static class PrewarmMissWatch
{
    [ThreadStatic] private static int t_count;
    [ThreadStatic] private static int t_threshold;
    [ThreadStatic] private static Action? t_onThreshold;

    public static void Arm(int threshold, Action onThreshold)
    {
        t_count = 0;
        t_threshold = threshold;
        t_onThreshold = onThreshold;
    }

    public static void Disarm() => t_onThreshold = null;

    /// <summary>One backing-store read by a prewarm scope on this thread.</summary>
    public static void Miss()
    {
        Action? callback = t_onThreshold;
        if (callback is null || ++t_count < t_threshold) return;
        t_onThreshold = null;
        callback();
    }
}
