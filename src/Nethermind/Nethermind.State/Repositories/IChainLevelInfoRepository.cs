// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.State.Repositories
{
    public interface IChainLevelInfoRepository
    {
        void Delete(ulong number, BatchWrite? batch = null);
        void PersistLevel(ulong number, ChainLevelInfo level, BatchWrite? batch = null);

        /// <summary>Publishes a level immediately and schedules its persistence when supported.</summary>
        /// <remarks>The scheduler must retain failed writes and drain them before state persistence.</remarks>
        void PersistLevelDeferred(ulong number, ChainLevelInfo level, Action<Action> enqueue, BatchWrite? batch = null) => PersistLevel(number, level, batch);
        BatchWrite StartBatch();

        /// <summary>Locks a deferred level update without requiring a native database batch.</summary>
        BatchWrite StartDeferredBatch() => StartBatch();
        ChainLevelInfo? LoadLevel(ulong number);
        IOwnedReadOnlyList<ChainLevelInfo?> MultiLoadLevel(in ArrayPoolListRef<ulong> blockNumbers);
    }
}
