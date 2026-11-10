// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Runtime.ExceptionServices;

namespace Nethermind.State.Pbt.Image;

/// <summary>Passes <paramref name="source"/> through, handing each item it yields to the follower taken with <see cref="Tee"/>.</summary>
/// <remarks>
/// The follower may be consumed on another thread. It yields each item only once this enumerator has, and this enumerator
/// blocks once the follower is <paramref name="capacity"/> items behind. Only this enumerator reads the source, and
/// disposing it disposes the source. A source failure is rethrown by the follower once it has drained what was read
/// before it; disposing this enumerator before the source ends cancels the follower the same way.
/// </remarks>
public sealed class TeeEnumerator<T>(IEnumerator<T> source, int capacity) : IEnumerator<T>
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object _gate = new();
    private readonly Queue<T> _pending = new();
    private Follower? _follower;
    private bool _started;
    private bool _ended;
    private bool _disposed;
    private bool _followerDisposed;
    private int _waiting;
    private ExceptionDispatchInfo? _failure;

    public T Current { get; private set; } = default!;

    object? IEnumerator.Current => Current;

    /// <summary>The follower of this enumerator; taken once, before the first <see cref="MoveNext"/>.</summary>
    public IEnumerator<T> Tee()
    {
        lock (_gate)
        {
            if (_follower is not null || _started) throw new InvalidOperationException("A tee is taken once, before the first item.");
            return _follower = new Follower(this);
        }
    }

    public bool MoveNext()
    {
        lock (_gate)
        {
            _started = true;
            while (IsFollowed && _pending.Count >= _capacity) Wait();
        }

        bool moved;
        try
        {
            moved = source.MoveNext();
            if (moved) Current = source.Current;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _failure = ExceptionDispatchInfo.Capture(exception);
                Wake();
            }
            throw;
        }

        lock (_gate)
        {
            if (!moved) _ended = true;
            else if (IsFollowed) _pending.Enqueue(Current);
            Wake();
        }
        return moved;
    }

    public void Reset() => throw new NotSupportedException();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Wake();
        }
        source.Dispose();
    }

    private bool IsFollowed => _follower is not null && !_followerDisposed;

    private bool TryTake(out T item)
    {
        lock (_gate)
        {
            while (!_pending.TryDequeue(out item!))
            {
                _failure?.Throw();
                if (_ended) return false;
                if (_disposed) throw new OperationCanceledException("The leading enumerator was disposed before its source ended.");
                Wait();
            }
            Wake();
            return true;
        }
    }

    private void ReleaseFollower()
    {
        lock (_gate)
        {
            _followerDisposed = true;
            _pending.Clear();
            Wake();
        }
    }

    private void Wait()
    {
        _waiting++;
        Monitor.Wait(_gate);
        _waiting--;
    }

    private void Wake()
    {
        if (_waiting != 0) Monitor.PulseAll(_gate);
    }

    private sealed class Follower(TeeEnumerator<T> leader) : IEnumerator<T>
    {
        public T Current { get; private set; } = default!;

        object? IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (!leader.TryTake(out T item)) return false;
            Current = item;
            return true;
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose() => leader.ReleaseFollower();
    }
}
