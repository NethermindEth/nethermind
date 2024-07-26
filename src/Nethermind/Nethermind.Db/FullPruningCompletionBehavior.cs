// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db
{
    /// <summary>
    /// Defines what to do when a full prune completes.
    /// </summary>
    public enum FullPruningCompletionBehavior
    {
        /// <summary>
        /// Do nothing once pruning is completed.
        /// </summary>
        None,

        /// <summary>
        /// Shut Nethermind down gracefully if pruning was successful, but leave it running if it failed.
        /// </summary>
        ShutdownOnSuccess,

        /// <summary>
        /// Shut Nethermind down gracefully when pruning completes, regardless of whether or not it succeeded.
        /// </summary>
        AlwaysShutdown
    }
}
