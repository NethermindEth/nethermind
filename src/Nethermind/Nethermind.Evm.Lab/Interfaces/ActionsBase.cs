// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nethermind.Evm.Lab.Interfaces;
public abstract record ActionsBase;

public class EventsSink
{
    private Stopwatch watch = new Stopwatch();
    private ConcurrentQueue<ActionsBase> Events { get; set; } = new();
    public void EnqueueEvent(ActionsBase msg, bool overrideTimeout = false)
    {
        if (!watch.IsRunning || watch.ElapsedMilliseconds > 100 || overrideTimeout)
        {
            Events.Enqueue(msg);
            if (!watch.IsRunning) watch.Start();
            else watch.Restart();
        }
    }

    public bool TryDequeueEvent(out ActionsBase? msg)
    {
        return Events.TryDequeue(out msg);
    }

    public void EmptyQueue() => Events.Clear();
}
