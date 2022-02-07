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

namespace Nethermind.AccountAbstraction.Network
{
    public static class AaMessageCode
    {
        public const int UserOperations = 0x00;
        
        // more UserOperations-connected messages are planned to be added in the future
        // probably as a higher version of AaProtocolHandler. Commented out for now
        
        // public const int NewPooledUserOperationsHashes  = 0xab;
        // public const int GetPooledUserOperations = 0xac;
        // public const int PooledUserOperations  = 0xad;
    }
}
