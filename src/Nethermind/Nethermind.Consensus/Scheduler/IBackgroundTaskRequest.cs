// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Consensus.Scheduler;

/// <summary>
/// Marks a request type as schedulable on <see cref="IBackgroundTaskScheduler"/> and names the bucket
/// its queue depth is reported under.
/// </summary>
/// <remarks>
/// Implement as <c>public static int TaskId =&gt; BackgroundTaskTypeId&lt;TNamed&gt;.Id;</c>. A wrapper request
/// should point at the type it wraps, so that stats read as the underlying message name instead of the
/// wrapper's generic type name, which is identical for every instantiation.
/// </remarks>
public interface IBackgroundTaskRequest<T> where T : IBackgroundTaskRequest<T>
{
    static abstract int TaskId { get; }
}

/// <summary>
/// Assigns a dense id to <typeparamref name="T"/> on first use.
/// </summary>
public static class BackgroundTaskTypeId<T>
{
    public static readonly int Id = BackgroundTaskTypeRegistry.Register(typeof(T));
}

/// <summary>
/// Resolves the ids handed out by <see cref="BackgroundTaskTypeId{T}"/> back to type names for diagnostics.
/// </summary>
public static class BackgroundTaskTypeRegistry
{
    /// <remarks>
    /// Ids beyond this stay valid but are neither named nor counted. The repo registers ~25 request types,
    /// so the cap only trades a growable table for a fixed one on the scheduling hot path.
    /// </remarks>
    public const int MaxTaskTypes = 64;

    private static readonly Type?[] Types = new Type?[MaxTaskTypes];
    private static int _lastId = -1;

    /// <summary>Number of ids assigned so far, capped at <see cref="MaxTaskTypes"/>.</summary>
    public static int Count => Math.Min(Volatile.Read(ref _lastId) + 1, MaxTaskTypes);

    internal static int Register(Type type)
    {
        int id = Interlocked.Increment(ref _lastId);
        if ((uint)id < MaxTaskTypes)
        {
            Types[id] = type;
        }

        return id;
    }

    public static string GetName(int id) => ((uint)id < MaxTaskTypes ? Types[id]?.Name : null) ?? "unknown";
}
