// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

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

    [ThreadStatic] private static CancellationToken t_cancel;
    [ThreadStatic] private static bool t_cancelArmed;

    /// <summary>Until <see cref="ClearCancel"/>, a backing-store read on this thread throws once the token is cancelled.</summary>
    public static void CancelAt(CancellationToken token)
    {
        t_cancel = token;
        t_cancelArmed = true;
    }

    public static void ClearCancel()
    {
        t_cancelArmed = false;
        t_cancel = default;
    }

    public static void ThrowIfCancelled()
    {
        if (t_cancelArmed && t_cancel.IsCancellationRequested) throw new OperationCanceledException(t_cancel);
    }

    /// <summary>One backing-store read by a prewarm scope on this thread.</summary>
    public static void Miss()
    {
        ThrowIfCancelled();
        Action? callback = t_onThreshold;
        if (callback is null || ++t_count < t_threshold) return;
        t_onThreshold = null;
        callback();
    }
}
