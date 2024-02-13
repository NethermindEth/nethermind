// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Synchronization.RangeSync;

public interface IRangeProgressTracker<T>: IRangeFinishTracker
{
    public bool CanSync();
    public void UpdatePivot();

    public (T request, bool finished) GetNextRequest();
}

public interface IRangeFinishTracker
{
    public bool IsGetRangesFinished();
}
