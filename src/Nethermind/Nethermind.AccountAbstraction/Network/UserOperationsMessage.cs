// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
