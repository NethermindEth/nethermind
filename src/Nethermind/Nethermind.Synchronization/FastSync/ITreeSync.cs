// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

using Nethermind.Core;

namespace Nethermind.Synchronization.FastSync;

public interface ITreeSync
{
    public event EventHandler<SyncCompletedEventArgs> SyncCompleted;

    public class SyncCompletedEventArgs(BlockHeader header) : EventArgs
    {
        public BlockHeader Pivot => header;
    }
}
