// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Packet.Headers;
using Lantern.Discv5.WireProtocol.Packet.Types;
using Lantern.Discv5.WireProtocol.Session;
using Microsoft.Extensions.Logging;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.Discovery.Portal.LanternAdapter;

/// <summary>
/// So the original implementation of the session manager will have problem where the received decoded
/// endpoint from FindNode/FindContent is decoded to ipv4 instead of ipv6 which is returned by .net stack.
/// So the IPEndPoint does not match which causes further GetSession to not work. Hence, this class to normalize
/// it so that the session is found.
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
    private readonly ISessionKeys _sessionKeys = options.SessionKeys;

    private readonly LruCache<SessionCacheKey, ISessionMain> _sessions = new(options.SessionCacheSize);

    private ILogger _logger = loggerFactory.CreateLogger<SessionManagerNormalizer>();

    public ISessionMain? GetSession(byte[] nodeId, IPEndPoint endPoint)
    {
        if (endPoint.AddressFamily == AddressFamily.InterNetwork)
        {
            endPoint = new IPEndPoint(endPoint.Address.MapToIPv6(), endPoint.Port);
        }

        var key = new SessionCacheKey(nodeId, endPoint);
        return _sessions.Get(key);
    }

    public int TotalSessionCount => _sessions.Count;

    public ISessionMain CreateSession(SessionType sessionType, byte[] nodeId, IPEndPoint endPoint)
    {
        _logger.LogInformation("Creating session {sessionType} {nodeId}", sessionType, nodeId.ToHexString());
        if (endPoint.AddressFamily == AddressFamily.InterNetwork)
        {
            endPoint = new IPEndPoint(endPoint.Address.MapToIPv6(), endPoint.Port);
        }

        var key = new SessionCacheKey(nodeId, endPoint);
        var session = _sessions.Get(key);

        if (session != null)
            return session;

        var newSession = CreateSession(sessionType);
        _sessions.Add(key, newSession);
        session = newSession;

        return session;
    }

    private ISessionMain CreateSession(SessionType sessionType)
    {
        var newSessionKeys = new SessionKeys(_sessionKeys.PrivateKey);
        var cryptoSession = new SessionMain(newSessionKeys, aesCrypto, sessionCrypto, loggerFactory, sessionType);
        return new EasilyEstablishedSession(cryptoSession);
    }
}

// So once it received a handshake, it is still not considered established, which seems to be only
// set when an ordinary message is successfully decrypted.
// This override that.
public class EasilyEstablishedSession(ISessionMain baseSession) : ISessionMain
{
    private bool _wasDecryptedWithNewKey = false;

    public void SetChallengeData(byte[] maskingIv, byte[] header)
    {
        baseSession.SetChallengeData(maskingIv, header);
    }

    public byte[]? GenerateIdSignature(byte[] destNodeId)
    {
        return baseSession.GenerateIdSignature(destNodeId);
    }

    public bool VerifyIdSignature(HandshakePacketBase handshakePacket, byte[] publicKey, byte[] selfNodeId)
    {
        return baseSession.VerifyIdSignature(handshakePacket, publicKey, selfNodeId);
    }

    public byte[]? EncryptMessageWithNewKeys(IEnr dest, StaticHeader header, byte[] selfNodeId, byte[] message, byte[] maskingIv)
    {
        return baseSession.EncryptMessageWithNewKeys(dest, header, selfNodeId, message, maskingIv);
    }

    public byte[]? DecryptMessageWithNewKeys(StaticHeader header, byte[] maskingIv, byte[] encryptedMessage,
        HandshakePacketBase handshakePacket, byte[] selfNodeId)
    {
        var res = baseSession.DecryptMessageWithNewKeys(header, maskingIv, encryptedMessage, handshakePacket, selfNodeId);
        if (res != null)
        {
            _wasDecryptedWithNewKey = true;
        }

        return res;
    }

    public byte[]? EncryptMessage(StaticHeader header, byte[] maskingIv, byte[] rawMessage)
    {
        return baseSession.EncryptMessage(header, maskingIv, rawMessage);
    }

    public byte[]? DecryptMessage(StaticHeader header, byte[] maskingIv, byte[] encryptedMessage)
    {
        return baseSession.DecryptMessage(header, maskingIv, encryptedMessage);
    }

    public bool IsEstablished => baseSession.IsEstablished || _wasDecryptedWithNewKey;

    public byte[] MessageCount => baseSession.MessageCount;

    public byte[] PublicKey => baseSession.PublicKey;

    public byte[] EphemeralPublicKey => baseSession.EphemeralPublicKey;
}
