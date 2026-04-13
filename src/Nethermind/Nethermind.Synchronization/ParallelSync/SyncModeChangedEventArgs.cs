// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Synchronization.ParallelSync
{
    public class SyncModeChangedEventArgs(SyncMode previous, SyncMode current) : EventArgs
    {
        public SyncMode Previous { get; } = previous;
        public SyncMode Current { get; } = current;

        public bool WasModeFinished(SyncMode mode)
        {
            return (Previous & mode) != 0 && (Current & mode) == 0;
        }
    }
}
