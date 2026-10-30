// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Grpc.Core;

namespace Nethermind.Eez.Attester;

internal sealed class BackendAdmission(ILogger<BackendAdmission> logger)
{
    private readonly ILogger<BackendAdmission> _logger = logger;
    private readonly object _lock = new();
    private TaskCompletionSource? _idle;

    internal BackendLease? TryAcquire()
    {
        lock (_lock)
        {
            if (_idle is not null) return null;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new BackendLease(this);
        }
    }

    internal async Task<BackendLease> Acquire(CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            BackendLease? lease = TryAcquire();
            if (lease is not null) return lease;
            await WaitUntilIdle().WaitAsync(cancellation).ConfigureAwait(false);
        }
    }

    internal Task WaitUntilIdle()
    {
        lock (_lock) return _idle?.Task ?? Task.CompletedTask;
    }

    private void Release()
    {
        lock (_lock)
        {
            TaskCompletionSource idle = _idle ?? throw new InvalidOperationException("backend already idle");
            _idle = null;
            idle.SetResult();
        }
    }

    internal sealed class BackendLease(BackendAdmission admission) : IDisposable
    {
        private BackendAdmission? _admission = admission;
        private readonly ILogger<BackendAdmission> _logger = admission._logger;

        internal async Task ReleaseAfter(Task worker, Action cleanup)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (exception is RpcException or OperationCanceledException)
                    _logger.LogDebug(exception, "attestation worker stopped");
                else
                    _logger.LogError(exception, "attestation worker failed");
            }
            finally
            {
                try { cleanup(); }
                catch (Exception exception) { _logger.LogError(exception, "attestation worker cleanup failed"); }
                finally { Dispose(); }
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _admission, null)?.Release();
    }
}
