// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.Core.Extensions;

public static class ValueTaskWaitExtensions
{
    /// <summary>Blocks the current thread until <paramref name="valueTask"/> completes.</summary>
    /// <remarks>Can allocate a new <see cref="Task"/> instance in some cases.</remarks>
    /// <param name="valueTask">The value task to wait on. Must not have been consumed yet.</param>
    public static void SafeWait(this ValueTask valueTask)
    {
        if (valueTask.IsCompleted)
        {
            valueTask.GetAwaiter().GetResult();
            return;
        }

        valueTask.AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Blocks the current thread until <paramref name="valueTask"/> completes, discarding its result.</summary>
    /// <remarks>Can allocate a new <see cref="Task"/> instance in some cases.</remarks>
    /// <param name="valueTask">The value task to wait on. Must not have been consumed yet.</param>
    public static void SafeWait<TResult>(this ValueTask<TResult> valueTask) => valueTask.SafeGetResult();

    /// <summary>Blocks the current thread until <paramref name="valueTask"/> completes, then returns its result.</summary>
    /// <remarks>Can allocate a new <see cref="Task"/> instance in some cases.</remarks>
    /// <param name="valueTask">The value task to wait on. Must not have been consumed yet.</param>
    public static TResult SafeGetResult<TResult>(this ValueTask<TResult> valueTask) => valueTask.IsCompleted
        ? valueTask.GetAwaiter().GetResult()
        : valueTask.AsTask().GetAwaiter().GetResult();
}
