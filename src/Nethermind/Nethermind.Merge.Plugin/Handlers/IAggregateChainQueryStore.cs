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

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// Interface for a store that is able to execute an aggregate query from root (as far it knows) down to a node,
/// represented by a key.
///
/// Used for querying things such as the lowest (closest to root) invalid node in a chain.
/// </summary>
/// <typeparam name="TKey">Key for node (probably Keccak)</typeparam>
/// <typeparam name="TValue">Value for a node (implementation specific)</typeparam>
/// <typeparam name="TAggregate">Aggregated result type for a chain (implementation specific)</typeparam>
public interface IAggregateChainQueryStore<in TKey, in TValue, out TAggregate>
{
    public void SetChildParent(TKey child, TKey parent);
    public void SetValue(TKey item, TValue value);
    public TAggregate? QueryUpTo(TKey item);
}
