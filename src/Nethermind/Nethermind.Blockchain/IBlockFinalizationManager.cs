// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain
{
    public interface IBlockFinalizationManager : IDisposable
    {
        /// <summary>
        /// Last level that was finalize while processing blocks. This level will not be reorganised.
        /// </summary>
        long LastFinalizedBlockLevel { get; }
        event EventHandler<FinalizeEventArgs> BlocksFinalized;

        public bool IsFinalized(long level) => LastFinalizedBlockLevel >= level;
    }
}
