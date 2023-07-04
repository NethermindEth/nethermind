// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.FullPruning
{
    /// <summary>
    /// Database wrapper for full pruning.
    /// </summary>
    public interface IFullPruningDb
    {
        /// <summary>
        /// Are we able to start full pruning.
        /// </summary>
        bool CanStartPruning { get; }

        /// <summary>
        /// Try starting full pruning.
        /// </summary>
        /// <param name="duplicateReads">If pruning should duplicate db reads.</param>
        /// <param name="context">Out, context of pruning.</param>
        /// <returns>true if pruning was started, false otherwise.</returns>
        bool TryStartPruning(bool duplicateReads, out IPruningContext context);

        /// <summary>
        /// Gets the path to current DB using base path.
        /// </summary>
        /// <param name="basePath"></param>
        /// <returns></returns>
        string GetPath(string basePath);

        /// <summary>
        /// Gets the name of inner DB.
        /// </summary>
        string InnerDbName { get; }

        event EventHandler<PruningEventArgs> PruningStarted;
        event EventHandler<PruningEventArgs> PruningFinished;
    }
}
