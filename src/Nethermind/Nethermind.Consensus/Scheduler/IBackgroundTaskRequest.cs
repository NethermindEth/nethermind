// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Consensus.Scheduler;

/// <summary>
/// Marker for request types scheduled via <see cref="IBackgroundTaskScheduler"/>.
/// </summary>
/// <remarks>
/// <see cref="TaskId"/> is a unique integer assigned to the implementing type the first time
/// <see cref="BackgroundTaskTypeId{T}.Id"/> is read. The scheduler keys stats and dropped-task
/// counters by this int to avoid hashing the type or its string representation on every enqueue.
/// </remarks>
public interface IBackgroundTaskRequest<T> where T : IBackgroundTaskRequest<T>
{
    static abstract int TaskId { get; }
}

/// <summary>
/// Lazy holder that assigns a unique <see cref="Id"/> to <typeparamref name="T"/> on first access
/// and registers it with <see cref="BackgroundTaskTypeRegistry"/>.
/// </summary>
public static class BackgroundTaskTypeId<T> where T : IBackgroundTaskRequest<T>
{
    public static readonly int Id = BackgroundTaskTypeRegistry.Register(typeof(T));
}

/// <summary>
/// Maps task ids back to <see cref="Type"/> for diagnostics (drop-warning rendering, stats dumps).
/// Ids are produced by <see cref="Interlocked.Increment(ref int)"/>, so they are dense from 0 upward.
/// </summary>
public static class BackgroundTaskTypeRegistry
{
    private static int s_next = -1;
    private static Type?[] s_types = new Type?[16];
    private static readonly Lock s_growLock = new();

    internal static int Register(Type type)
    {
        int id = Interlocked.Increment(ref s_next);
        EnsureCapacity(id + 1);
        Volatile.Read(ref s_types)[id] = type;
        return id;
    }

    public static Type? GetType(int id)
    {
        Type?[] types = Volatile.Read(ref s_types);
        return (uint)id < (uint)types.Length ? types[id] : null;
    }

    public static string GetName(int id) => GetType(id)?.Name ?? "unknown";

    private static void EnsureCapacity(int needed)
    {
        Type?[] current = Volatile.Read(ref s_types);
        if (current.Length >= needed) return;

        lock (s_growLock)
        {
            current = s_types;
            if (current.Length >= needed) return;

            int newLength = current.Length;
            while (newLength < needed) newLength *= 2;
            Type?[] grown = new Type?[newLength];
            Array.Copy(current, grown, current.Length);
            Volatile.Write(ref s_types, grown);
        }
    }
}
