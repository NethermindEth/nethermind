using System.Net;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol.Session;

public class SessionManager(SessionOptions options, IAesCrypto aesCrypto, ISessionCrypto sessionCrypto,
        ILoggerFactory loggerFactory)
    : ISessionManager
{
    private readonly ISessionKeys _sessionKeys = options.SessionKeys!;

    private readonly LruCache<SessionCacheKey, ISessionMain> _sessions = new(options.SessionCacheSize);

    public int TotalSessionCount => _sessions.Count;

    public ISessionMain CreateSession(SessionType sessionType, byte[] nodeId, IPEndPoint endPoint)
    {
        var key = new SessionCacheKey(nodeId, endPoint);
        var session = _sessions.Get(key);

        if (session != null)
            return session;

        var newSession = CreateSession(sessionType);
        _sessions.Add(key, newSession);
        session = newSession;

        return session;
    }

    public ISessionMain? GetSession(byte[] nodeId, IPEndPoint endPoint)
    {
        var key = new SessionCacheKey(nodeId, endPoint);

        return _sessions.Get(key);
    }

    private ISessionMain CreateSession(SessionType sessionType)
    {
        var newSessionKeys = new SessionKeys(_sessionKeys.PrivateKey);
        var cryptoSession = new SessionMain(newSessionKeys, aesCrypto, sessionCrypto, loggerFactory, sessionType);

        return cryptoSession;
    }
}
