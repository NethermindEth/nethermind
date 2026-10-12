// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;

namespace Nethermind.Network
{
    public interface IProtocolValidator
    {
        /// <summary>
        /// Validates the initialized protocol and disconnects the session when it is invalid.
        /// </summary>
        /// <returns><c>true</c> if the session passed validation and survived; otherwise <c>false</c>.</returns>
        bool ValidateOrDisconnect(string protocol, ISession session, ProtocolInitializedEventArgs eventArgs);
    }
}
