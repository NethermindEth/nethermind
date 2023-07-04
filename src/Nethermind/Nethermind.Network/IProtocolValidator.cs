// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;

namespace Nethermind.Network
{
    public interface IProtocolValidator
    {
        bool DisconnectOnInvalid(string protocol, ISession session, ProtocolInitializedEventArgs eventArgs);
    }
}
