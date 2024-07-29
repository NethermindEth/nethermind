// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using Lantern.Discv5.WireProtocol.Session;
using Microsoft.Extensions.Logging;

namespace Nethermind.Network.Discovery.Portal;

public class SessionManagerNormalizer(
    SessionOptions options,
    IAesCrypto aesCrypto,
    ISessionCrypto sessionCrypto,
    ILoggerFactory loggerFactory): ISessionManager
{
    private ISessionManager _baseImplementation = new SessionManager(options, aesCrypto, sessionCrypto, loggerFactory);
    public ISessionMain? GetSession(byte[] nodeId, IPEndPoint endPoint)
    {
        if (endPoint.AddressFamily == AddressFamily.InterNetwork)
        {
            endPoint = new IPEndPoint(endPoint.Address.MapToIPv6(), endPoint.Port);
        }

        return _baseImplementation.GetSession(nodeId, endPoint);
    }

    public ISessionMain CreateSession(SessionType sessionType, byte[] nodeId, IPEndPoint endPoint)
    {
        return _baseImplementation.CreateSession(sessionType, nodeId, endPoint);
    }

    public int TotalSessionCount => _baseImplementation.TotalSessionCount;
}
