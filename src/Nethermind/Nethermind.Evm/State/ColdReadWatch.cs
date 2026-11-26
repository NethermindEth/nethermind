// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.State;

/// <summary>Told when a prewarm run armed with <see cref="ColdReadWatch.Arm"/> reaches its threshold of cold reads.</summary>
public interface IColdReadHandler
{
    /// <param name="index">The index the run was armed with, such as its transaction's position in the block.</param>
    /// <param name="item">The item the run was armed with, such as its transaction.</param>
    void OnColdReads(int index, object? item);
}

/// <summary>
/// Counts the backing-store storage reads a prewarm run makes on its thread, and tells a handler once when they reach a
/// threshold, so a run caught in a chain of cold reads can hand its transaction to storage discovery.
/// </summary>
/// <remarks>
/// Thread-static: the counting and the call belong to the run on the calling thread only. The handler and its arguments
/// are held as they are rather than in a closure, because every warmed transaction arms the watch.
/// </remarks>
public static class ColdReadWatch
{
    [ThreadStatic] private static int t_count;
    [ThreadStatic] private static int t_threshold;
    [ThreadStatic] private static IColdReadHandler? t_handler;
    [ThreadStatic] private static int t_index;
    [ThreadStatic] private static object? t_item;

    /// <summary>Starts counting this thread's cold reads; the read that reaches <paramref name="threshold"/> tells <paramref name="handler"/>.</summary>
    public static void Arm(int threshold, IColdReadHandler handler, int index, object? item)
    {
        t_count = 0;
        t_threshold = threshold;
        t_index = index;
        t_item = item;
        t_handler = handler;
    }

    /// <summary>Stops counting on this thread.</summary>
    public static void Disarm()
    {
        t_handler = null;
        t_item = null;
    }

    /// <summary>Records one backing-store read by a prewarm scope on this thread.</summary>
    public static void Read()
    {
        IColdReadHandler? handler = t_handler;
        if (handler is null || ++t_count < t_threshold) return;
        object? item = t_item;
        Disarm();
        handler.OnColdReads(t_index, item);
    }
}
