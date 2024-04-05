// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain.FullPruning;

/// <summary>
/// Allows to have multiple <see cref="IPruningTrigger"/>s.
/// </summary>
public class CompositePruningTrigger : IPruningTrigger
{
    /// <summary>
    /// Adds new <see cref="IPruningTrigger"/> to the be watched."/>
    /// </summary>
    /// <param name="trigger">trigger to be watched</param>
    public void Add(IPruningTrigger trigger)
    {
        trigger.Prune += OnPrune;
    }

    private void OnPrune(object? sender, PruningTriggerEventArgs e)
    {
        Prune?.Invoke(sender, e);
    }

    /// <inheridoc /> 
    public event EventHandler<PruningTriggerEventArgs>? Prune;
}
