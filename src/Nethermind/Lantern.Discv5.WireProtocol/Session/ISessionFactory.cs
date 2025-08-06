namespace Lantern.Discv5.WireProtocol.Session;

public interface ISessionFactory
{
    ISessionMain Create(SessionType sessionType);
}