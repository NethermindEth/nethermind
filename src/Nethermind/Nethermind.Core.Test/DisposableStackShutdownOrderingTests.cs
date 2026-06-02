// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using NUnit.Framework;

namespace Nethermind.Core.Test;

/// <summary>
/// Regression tests for the JSON-RPC shutdown ordering bug (#6597) where
/// <c>trace_replayBlockTransactions</c> could throw <see cref="ObjectDisposedException"/> because the
/// underlying database was disposed while an in-flight request was still reading from it.
/// </summary>
/// <remarks>
/// The fix pushes the JSON-RPC server (an <see cref="IAsyncDisposable"/>) onto the dispose stack instead
/// of a fire-and-forget <see cref="IDisposable"/> wrapper, so the disposer awaits the request drain to
/// completion before the stores registered earlier are disposed. These tests pin that contract against
/// the real Autofac disposer that <see cref="AutofacDisposableStack"/> wraps.
/// </remarks>
[Parallelizable(ParallelScope.Self)]
public class DisposableStackShutdownOrderingTests
{
    [Test]
    public async Task AsyncTeardown_pushed_after_store_completes_before_store_is_disposed()
    {
        await using ILifetimeScope scope = new ContainerBuilder().Build();
        AutofacDisposableStack disposeStack = new(scope);

        TaskCompletionSource drainEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseDrain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool storeDisposed = false;
        bool drainFinishedBeforeStoreDisposed = false;

        // Mimics a DB / store: registered first, so it disposes last (LIFO).
        disposeStack.Push(new Reactive.AnonymousDisposable(() => storeDisposed = true));

        // Mimics the JSON-RPC server graceful drain: registered last, so it disposes first.
        disposeStack.Push(new AsyncDisposableAction(async () =>
        {
            drainEntered.SetResult();
            await releaseDrain.Task;
            drainFinishedBeforeStoreDisposed = !storeDisposed;
        }));

        ValueTask disposeTask = scope.DisposeAsync();

        // Wait until the drain is actually running before asserting. Otherwise the test would pass even
        // under a hypothetical disposer that hadn't started the async teardown yet.
        await drainEntered.Task;
        Assert.That(storeDisposed, Is.False, "the store must stay alive until the in-flight request drain finishes");

        releaseDrain.SetResult();
        await disposeTask;

        Assert.That(drainFinishedBeforeStoreDisposed, Is.True);
        Assert.That(storeDisposed, Is.True);
    }

    [Test]
    public async Task InFlight_reader_does_not_observe_store_disposed_during_async_teardown()
    {
        await using ILifetimeScope scope = new ContainerBuilder().Build();
        AutofacDisposableStack disposeStack = new(scope);

        FakeStore store = new();
        TaskCompletionSource readerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseReader = new();
        Exception? readerError = null;

        disposeStack.Push(store);

        // The server teardown drains the in-flight reader before returning, just like Kestrel's graceful stop.
        disposeStack.Push(new AsyncDisposableAction(async () =>
        {
            Task reader = Task.Run(() =>
            {
                readerStarted.SetResult();
                releaseReader.Wait();
                try
                {
                    store.Read();
                }
                catch (Exception e)
                {
                    readerError = e;
                }
            });

            await readerStarted.Task;
            releaseReader.Set();
            await reader;
        }));

        await scope.DisposeAsync();

        Assert.That(readerError, Is.Null, "the store must not be disposed while a request is still reading from it");
        Assert.That(store.Disposed, Is.True);
    }

    private sealed class AsyncDisposableAction(Func<ValueTask> action) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => action();
    }

    private sealed class FakeStore : IDisposable
    {
        private int _disposed;

        public bool Disposed => Volatile.Read(ref _disposed) == 1;

        public void Read() => ObjectDisposedException.ThrowIf(Disposed, this);

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
