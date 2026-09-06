// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;

namespace Nethermind.Consensus.Scheduler;

/// <summary>
/// Marks a request type as schedulable on <see cref="IBackgroundTaskScheduler"/> and names the bucket
/// its queue depth is reported under.
/// </summary>
/// <remarks>
/// The default reports under the implementing type's own name. A wrapper request should override with
/// <c>BackgroundTaskTypeId&lt;TWrapped&gt;.Id</c>, since its own generic type name is identical for every
/// instantiation. Conversely, one request type scheduled for two unrelated workloads should be split in
/// two, so that each reports separately.
/// </remarks>
public interface IBackgroundTaskRequest<T> where T : IBackgroundTaskRequest<T>
{
    static virtual int TaskId => BackgroundTaskTypeId<T>.Id;
}

/// <summary>
/// Assigns a dense id to <typeparamref name="T"/> on first use.
/// </summary>
public static class BackgroundTaskTypeId<T>
{
    public static readonly int Id = BackgroundTaskTypeRegistry.Register(typeof(T));
}

/// <summary>
/// Resolves the ids handed out by <see cref="BackgroundTaskTypeId{T}"/> back to names for diagnostics.
/// </summary>
/// <remarks>
/// Names are resolved once per type, at registration, so that rendering a stats line is a plain array read.
/// </remarks>
public static class BackgroundTaskTypeRegistry
{
    /// <remarks>
    /// Ids beyond this stay valid but are neither named nor counted. The repo registers ~25 request types,
    /// so the cap only trades a growable table for a fixed one on the scheduling hot path.
    /// </remarks>
    public const int MaxTaskTypes = 64;

    private static readonly Type?[] Types = new Type?[MaxTaskTypes];
    private static readonly string?[] Names = new string?[MaxTaskTypes];
    private static readonly Lock RegisterLock = new();
    private static int _lastId = -1;

    internal static int Register(Type type)
    {
        int id = Interlocked.Increment(ref _lastId);
        Debug.Assert(id < MaxTaskTypes, $"More than {MaxTaskTypes} background task types; raise {nameof(MaxTaskTypes)} or the extra ones go unreported.");

        if ((uint)id >= MaxTaskTypes) return id;

        // Registration happens once per type, from a static constructor on first schedule
        lock (RegisterLock)
        {
            string name = type.Name;
            for (int other = 0; other < MaxTaskTypes; other++)
            {
                // Simple names are not unique — eth/62 and eth/66 both declare a GetBlockHeadersMessage —
                // so a collision qualifies both this type and the one it collides with
                if (Types[other] is Type candidate && candidate.Name == name)
                {
                    name = type.FullName!;
                    Volatile.Write(ref Names[other], candidate.FullName);
                }
            }

            Types[id] = type;
            // Ordered after the type's name is resolved, and after the writes above, so a concurrent
            // GetName sees either a fully resolved name or nothing
            Volatile.Write(ref Names[id], name);
        }

        return id;
    }

    /// <summary>
    /// Name to report <paramref name="id"/> under, or <c>null</c> if no type has claimed it.
    /// </summary>
    public static string? GetName(int id) => (uint)id < MaxTaskTypes ? Volatile.Read(ref Names[id]) : null;
}
