// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.AccountAbstraction.Network;
using Nethermind.Core.Crypto;

namespace Nethermind.AccountAbstraction.Broadcaster
{
    public interface IUserOperationPoolPeer
    {
        public PublicKey Id { get; }
        public string Enode => string.Empty;
        void SendNewUserOperation(UserOperationWithEntryPoint uop);
        void SendNewUserOperations(IEnumerable<UserOperationWithEntryPoint> uops);
    }
}
