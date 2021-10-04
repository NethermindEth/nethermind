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

namespace Nethermind.Core
{
    /// <summary>
    /// Provides an implementation of an interface (or an instance of a class) depending on a switching event
    /// (e.g. the Merge transition).
    /// Created to support consensus switcher with IGossipPolicy implementations.
    /// </summary>
    /// <typeparam name="T">Type of the object for which to resolve implementations</typeparam>
    /// <typeparam name="TImplementation"></typeparam>
    /// <typeparam name="TSwitchingType"></typeparam>
    public interface ISwitchDependent<TImplementation, in TSwitchingType>
    {
        TImplementation Resolve();

        void Register(TImplementation implementation, TSwitchingType switchingItem);
    }
}
