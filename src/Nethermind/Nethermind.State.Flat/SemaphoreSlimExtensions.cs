// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.State.Flat;

internal static class SemaphoreSlimExtensions
{
    /// <summary>Synchronously acquire <paramref name="semaphore"/> and return a scope that releases it on
    /// dispose, so a <c>using</c> statement replaces an explicit Wait/try-finally/Release — mirroring
    /// <see cref="System.Threading.Lock.EnterScope"/> for a semaphore also held across awaits elsewhere.</summary>
    public static Scope EnterScope(this SemaphoreSlim semaphore)
    {
        semaphore.Wait();
        return new Scope(semaphore);
    }

    public readonly struct Scope(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
