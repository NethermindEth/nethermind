// Apache 2.0 Reactive from .NET Foundation copy

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core
{
    public class Reactive
    {
        /// <summary>
        /// Represents an Action-based disposable.
        /// </summary>
        /// <remarks>
        /// Constructs a new disposable with the given action used for disposal.
        /// </remarks>
        /// <param name="dispose">Disposal action which will be run upon calling Dispose.</param>
        public sealed class AnonymousDisposable(Action dispose) : IDisposable
        {
            private volatile Action? _dispose = dispose;

            /// <summary>
            /// Gets a value that indicates whether the object is disposed.
            /// </summary>
            public bool IsDisposed => _dispose is null;

            /// <summary>
            /// Calls the disposal action if and only if the current instance hasn't been disposed yet.
            /// </summary>
            public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }

        /// <summary>
        /// Represents a <see cref="Func{TResult}"/>-based asynchronous disposable.
        /// </summary>
        /// <remarks>
        /// Unlike wrapping an asynchronous teardown in <see cref="AnonymousDisposable"/> — where the returned
        /// <see cref="Task"/> would be discarded and never awaited — this awaits the teardown on
        /// <see cref="DisposeAsync"/>. Use it for resources that must be fully torn down before subsequent
        /// disposals run, e.g. draining in-flight requests before the underlying stores are closed.
        /// </remarks>
        /// <param name="dispose">Asynchronous disposal action which will be awaited upon calling <see cref="DisposeAsync"/>.</param>
        public sealed class AnonymousAsyncDisposable(Func<ValueTask> dispose) : IAsyncDisposable
        {
            private Func<ValueTask>? _dispose = dispose;

            /// <summary>
            /// Gets a value that indicates whether the object is disposed.
            /// </summary>
            public bool IsDisposed => _dispose is null;

            /// <summary>
            /// Awaits the disposal action if and only if the current instance hasn't been disposed yet.
            /// </summary>
            public ValueTask DisposeAsync() => Interlocked.Exchange(ref _dispose, null)?.Invoke() ?? ValueTask.CompletedTask;
        }
    }
}
