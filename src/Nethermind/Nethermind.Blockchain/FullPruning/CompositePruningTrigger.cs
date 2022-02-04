//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;

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
