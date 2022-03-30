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

using System.Collections.Generic;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Core;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.AccountAbstraction.Network
{
    public class UserOperationsMessage : P2PMessage
    {
        public override int PacketType { get; } = AaMessageCode.UserOperations;
        public override string Protocol { get; } = "aa";
        
        public IList<UserOperationWithEntryPoint> UserOperationsWithEntryPoint { get; }

        public UserOperationsMessage(IList<UserOperationWithEntryPoint> userOperations)
        {
            UserOperationsWithEntryPoint = userOperations;
        }

        public override string ToString() => $"{nameof(UserOperationsMessage)}({UserOperationsWithEntryPoint?.Count})";
    }

    public class UserOperationWithEntryPoint
    {
        public UserOperation UserOperation { get; }
        public Address EntryPoint { get; }

        public UserOperationWithEntryPoint(UserOperation userOperation, Address entryPoint)
        {
            UserOperation = userOperation;
            EntryPoint = entryPoint;
        }
    }
}
