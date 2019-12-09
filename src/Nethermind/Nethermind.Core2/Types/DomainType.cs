//  Copyright (c) 2018 Demerzel Solutions Limited
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

namespace Nethermind.Core2.Types
{
    // TKS: I have changed to enum but now not sure if that was a good choice
    // I may prefer Sly's solution with a static list as it make the type more in line with everything else
    // and does not require any special treatment when serializing
    public enum DomainType : uint
    {
        BeaconProposer = 0U,
        BeaconAttester = 1U,
        Randao = 2U,
        Deposit = 3U,
        VoluntaryExit = 4U,
    }
}