// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

/// <summary>
/// One <see cref="Engine"/> serving every transaction of a traced block. The block tracer owns the engine
/// while the block executes; each transaction trace leases it so its JavaScript result stays readable
/// until the trace is serialized. The engine is torn down when the owner and every lease have released
/// it, or at once when the block tracer is disposed.
/// </summary>
public sealed class SharedEngine(Engine engine) : IDisposable
{
    private int _holders = 1;
    private int _ownerReleased;

    public Engine Engine => engine;

    public IDisposable Lease()
    {
        Interlocked.Increment(ref _holders);
        return new EngineLease(this);
    }

    public void ReleaseOwner()
    {
        if (Interlocked.Exchange(ref _ownerReleased, 1) == 0)
        {
            Release();
        }
    }

    public void Dispose() => engine.Dispose();

    private void Release()
    {
        if (Interlocked.Decrement(ref _holders) == 0)
        {
            engine.Dispose();
        }
    }

    private sealed class EngineLease(SharedEngine shared) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                shared.Release();
            }
        }
    }
}
