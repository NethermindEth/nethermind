// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Db.FullPruning
{
    /// <summary>
    /// Context of Full pruning.
    /// </summary>
    public interface IPruningContext : IKeyValueStore, IDisposable
    {
        /// <summary>
        /// Commits pruning, marking the end of cloning state to new DB.
        /// </summary>
        void Commit();

        /// <summary>
        /// Marks that pruning is starting.
        /// </summary>
        void MarkStart();

        /// <summary>
        /// Allows cancelling pruning
        /// </summary>
        CancellationTokenSource CancellationTokenSource { get; }
    }
}
