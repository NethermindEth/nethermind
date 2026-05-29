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
/// The fix registers the JSON-RPC server teardown on the dispose stack as an <see cref="IAsyncDisposable"/>
/// (via <see cref="Reactive.AnonymousAsyncDisposable"/>) instead of a fire-and-forget <see cref="IDisposable"/>,
/// so the disposer awaits the request drain to completion before the stores registered earlier are disposed.
/// These tests pin that contract against the real Autofac disposer that <see cref="AutofacDisposableStack"/> wraps.
/// </remarks>
public class DisposableStackShutdownOrderingTests
{
    [Test]
    public async Task AsyncTeardown_pushed_after_store_completes_before_store_is_disposed()
    {
        await using ILifetimeScope scope = new ContainerBuilder().Build();
        AutofacDisposableStack disposeStack = new(scope);

        TaskCompletionSource releaseDrain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool storeDisposed = false;
        bool drainFinishedBeforeStoreDisposed = false;

        // Mimics a DB / store: registered first, so it disposes last (LIFO).
        disposeStack.Push(new Reactive.AnonymousDisposable(() => storeDisposed = true));

        // Mimics the JSON-RPC server graceful drain: registered last, so it disposes first.
        disposeStack.Push(new Reactive.AnonymousAsyncDisposable(async () =>
        {
            await releaseDrain.Task;
            drainFinishedBeforeStoreDisposed = !storeDisposed;
        }));

        ValueTask disposeTask = scope.DisposeAsync();

        // While the drain is still running the store must not yet be disposed.
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
        TaskCompletionSource releaseReader = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? readerError = null;

        disposeStack.Push(store);

        // The server teardown drains the in-flight reader before returning, just like Kestrel's graceful stop.
        disposeStack.Push(new Reactive.AnonymousAsyncDisposable(async () =>
        {
            Task reader = Task.Run(() =>
            {
                readerStarted.SetResult();
                releaseReader.Task.GetAwaiter().GetResult();
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
            releaseReader.SetResult();
            await reader;
        }));

        await scope.DisposeAsync();

        Assert.That(readerError, Is.Null, "the store must not be disposed while a request is still reading from it");
        Assert.That(store.Disposed, Is.True);
    }

    private sealed class FakeStore : IDisposable
    {
        private int _disposed;

        public bool Disposed => Volatile.Read(ref _disposed) == 1;

        public void Read() => ObjectDisposedException.ThrowIf(Disposed, this);

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
