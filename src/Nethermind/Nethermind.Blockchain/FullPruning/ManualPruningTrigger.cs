// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain.FullPruning;

/// <summary>
/// Allows manually trigger Full Pruning.
/// </summary>
public class ManualPruningTrigger : IPruningTrigger
{
    public event EventHandler<PruningTriggerEventArgs>? Prune;

    /// <summary>
    /// Triggers full pruning.
    /// </summary>
    /// <returns>Status of triggering full pruning.</returns>
    public PruningStatus Trigger()
    {
        PruningTriggerEventArgs args = new PruningTriggerEventArgs();
        Prune?.Invoke(this, args);
        return args.Status;
    }
}
