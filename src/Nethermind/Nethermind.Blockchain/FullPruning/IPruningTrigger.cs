// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain.FullPruning
{
    /// <summary>
    /// Triggers Full Pruning
    /// </summary>
    public interface IPruningTrigger
    {
        /// <summary>
        /// Triggers Full Pruning
        /// </summary>
        event EventHandler<PruningTriggerEventArgs> Prune;
    }
}
