// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Timers;

namespace Nethermind.Xdc;

public interface ITimeoutTimer
{
    event EventHandler<ElapsedEventArgs> TimeoutElapsed;
    void Reset(TimeSpan period);
    void Start(TimeSpan period);
    void TriggerTimeout();
}
