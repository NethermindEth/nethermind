// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using Lantern.Discv5.WireProtocol.Session;
using Microsoft.Extensions.Logging;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// So the original implementation of the session manager will have problem where the received decoded
/// endpoint from FindNode/FindContent is decoded to ipv4 instead of ipv6 which is returned by .net stack.
/// Hence, this class to normalize it so that the session is found.
/// </summary>
/// <param name="options"></param>
/// <param name="aesCrypto"></param>
/// <param name="sessionCrypto"></param>
/// <param name="loggerFactory"></param>
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
