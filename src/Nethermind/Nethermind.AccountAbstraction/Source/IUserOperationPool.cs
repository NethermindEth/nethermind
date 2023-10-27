// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;

namespace Nethermind.AccountAbstraction.Source
{
    public interface IUserOperationPool
    {
        ResultWrapper<Hash256> AddUserOperation(UserOperation userOperation);
        bool RemoveUserOperation(Hash256? userOperationHash);
        IEnumerable<UserOperation> GetUserOperations();
        Address EntryPoint();
        bool IncludesUserOperationWithSenderAndNonce(Address sender, UInt256 nonce);
        bool CanInsert(UserOperation userOperation);
        event EventHandler<UserOperationEventArgs> NewReceived;
        event EventHandler<UserOperationEventArgs> NewPending;
    }
}
