// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Threading;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>Owns cancellation and draining of a reactive prewarming pass.</summary>
/// <remarks>Start, completion, and disposal have a single owner; disposal may run on a different thread.</remarks>
internal sealed class PrewarmingSession(CancellationToken cancellationToken, ILogger logger) : IDisposable
{
    private static readonly ParallelOptions CoordinatorOptions = new() { MaxDegreeOfParallelism = 2 };
    private readonly CancellationTokenSource _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private ParallelUnbalancedWork.BackgroundWork? _work;
    private IDisposable? _resources;
    private bool _disposed;

    internal CancellationToken Token => _cancellation.Token;

    internal void Start(Action warm, IDisposable resources)
    {
        _resources = resources;
        _work = ParallelUnbalancedWork.BackgroundFor(0, 1, CoordinatorOptions, _ => Warm(warm));
    }

    // Disposal does not report faults, so a pass that escapes its own handling is logged instead of lost.
    private void Warm(Action warm)
    {
        try
        {
            warm();
        }
        catch (OperationCanceledException)
        {
            // Block processing has finished warming.
        }
        catch (Exception ex)
        {
            logger.DebugError("Error pre-warming caches", ex);
        }
    }

    internal void WaitForCompletion() => _work?.WaitForCompletion();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _cancellation.Cancel();
        }
        finally
        {
            try
            {
                // Abandon an unstarted coordinator; otherwise drain its nested workers before releasing resources.
                _work?.Dispose();
            }
            finally
            {
                try { _resources?.Dispose(); }
                finally { _cancellation.Dispose(); }
            }
        }
    }
}
