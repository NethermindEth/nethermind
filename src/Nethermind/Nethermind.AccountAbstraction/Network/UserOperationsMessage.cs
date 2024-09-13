// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.AccountAbstraction.Network
{
    public class UserOperationsMessage(IOwnedReadOnlyList<UserOperationWithEntryPoint> userOperations) : P2PMessage
    {
        public override int PacketType { get; } = AaMessageCode.UserOperations;
        public override string Protocol { get; } = "aa";

        public IOwnedReadOnlyList<UserOperationWithEntryPoint> UserOperationsWithEntryPoint { get; } = userOperations;

        public override void Dispose()
        {
            base.Dispose();
            UserOperationsWithEntryPoint.Dispose();
        }

        public override string ToString() => $"{nameof(UserOperationsMessage)}({UserOperationsWithEntryPoint?.Count})";
    }

    public class UserOperationWithEntryPoint(UserOperation userOperation, Address entryPoint)
    {
        public UserOperation UserOperation { get; } = userOperation;
        public Address EntryPoint { get; } = entryPoint;
    }
}
