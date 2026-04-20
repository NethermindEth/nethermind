// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain
{
    public interface IBlockFinalizationManager : IDisposable
    {
        /// <summary>
        /// Current finalized level tracked by the active finalization manager.
        /// </summary>
        long LastFinalizedBlockLevel { get; }
        event EventHandler<FinalizeEventArgs> BlocksFinalized;

        public bool IsFinalized(long level) => LastFinalizedBlockLevel >= level;
    }
}
