// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Network.P2P.ProtocolHandlers;

public class ReusableProtocolHandlerFactory<THandler>(
    Func<ISession, THandler> handlerFactory,
    string protocolCode,
    int? expectedVersion = null) : IProtocolHandlerFactory
    where THandler : IProtocolHandler
{
    public string ProtocolCode => protocolCode;

    public bool TryCreate(ISession session, int version, [NotNullWhen(true)] out IProtocolHandler? handler)
    {
        if (expectedVersion is not null && version != expectedVersion)
        {
            handler = null;
            return false;
        }
        handler = handlerFactory(session);
        return true;
    }
}
