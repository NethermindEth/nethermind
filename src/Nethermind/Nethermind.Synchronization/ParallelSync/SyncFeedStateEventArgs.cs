// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Synchronization.ParallelSync
{
    public class SyncFeedStateEventArgs : EventArgs
    {
        public SyncFeedStateEventArgs(SyncFeedState newState)
        {
            NewState = newState;
        }

        public SyncFeedState NewState { get; }
    }
}
