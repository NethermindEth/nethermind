// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.ExceptionServices;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class WalkSlots(int capacity) : IDisposable
{
    private readonly SemaphoreSlim _free = new(capacity, capacity);

    public void Take(CancellationToken token) => _free.Wait(token);

    public void Return() => _free.Release();

    public bool TryBorrow() => _free.Wait(0);

    public void Dispose() => _free.Dispose();
}

internal sealed class BorrowedWork(WalkSlots slots)
{
    private readonly List<Task> _live = [];
    private Exception? _failure;

    public bool TryStart(Action work)
    {
        _live.RemoveAll(static task => task.IsCompletedSuccessfully);
        if (!slots.TryBorrow()) return false;

        Task started;
        try
        {
            started = Task.Factory.StartNew(() =>
            {
                try
                {
                    work();
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref _failure, e, null);
                    throw;
                }
                finally
                {
                    slots.Return();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            slots.Return();
            throw;
        }

        _live.Add(started);
        return true;
    }

    public void ThrowIfFailed()
    {
        Exception? failure = Volatile.Read(ref _failure);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void Finish(Exception? primary)
    {
        if (_live.Count > 0) Task.WhenAll(_live).ContinueWith(static _ => { }, TaskContinuationOptions.ExecuteSynchronously).Wait();

        Exception? borrowed = Volatile.Read(ref _failure);
        Exception? failure = primary;
        if (primary is null || (primary is OperationCanceledException && borrowed is not null and not OperationCanceledException)) failure = borrowed ?? primary;
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
