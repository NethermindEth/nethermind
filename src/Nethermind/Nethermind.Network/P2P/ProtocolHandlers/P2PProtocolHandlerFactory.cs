// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P.ProtocolHandlers;

/// <summary>
/// Factory for creating P2P protocol handlers.
/// Accepts any protocol version since version validation happens after the Hello handshake.
/// </summary>
public class P2PProtocolHandlerFactory(Func<ISession, P2PProtocolHandler> handlerFactory) : IProtocolHandlerFactory
{
    public string ProtocolCode => Protocol.P2P;

    public bool TryCreate(ISession session, int version, [NotNullWhen(true)] out IProtocolHandler? handler)
    {
        handler = handlerFactory(session);
        return true;
    }
}
