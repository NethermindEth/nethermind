using System.Net;

namespace Lantern.Discv5.WireProtocol.Session;

public interface ISessionManager
{
    int TotalSessionCount { get; }

    ISessionMain? GetSession(byte[] nodeId, IPEndPoint endPoint);

    ISessionMain CreateSession(SessionType sessionType, byte[] nodeId, IPEndPoint endPoint);
}