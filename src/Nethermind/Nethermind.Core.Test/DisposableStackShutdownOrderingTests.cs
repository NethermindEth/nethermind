// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using NUnit.Framework;

namespace Nethermind.Core.Test;

// Regression for #6597: trace_replayBlockTransactions could observe a disposed DB during shutdown because the
// JSON-RPC server teardown was fire-and-forget. The fix pushes the server (IAsyncDisposable) onto the dispose
// stack so the disposer awaits the request drain before the stores registered earlier are disposed.
[Parallelizable(ParallelScope.Self)]
public class DisposableStackShutdownOrderingTests
{
    [Test]
    public async Task In_flight_reader_does_not_observe_store_disposed_during_async_teardown()
    {
        await using ILifetimeScope scope = new ContainerBuilder().Build();
        AutofacDisposableStack disposeStack = new(scope);

        FakeStore store = new();
        TaskCompletionSource readerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseReader = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? readerError = null;

        disposeStack.Push(store);

        // The server teardown drains the in-flight reader before returning, like Kestrel's graceful stop.
        disposeStack.Push(new AsyncDisposableAction(async () =>
        {
            Task reader = Task.Run(async () =>
            {
                readerStarted.SetResult();
                await releaseReader.Task;
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

        await scope.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(readerError, Is.Null, "the store must not be disposed while a request is still reading from it");
            Assert.That(store.Disposed, Is.True);
        }
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
