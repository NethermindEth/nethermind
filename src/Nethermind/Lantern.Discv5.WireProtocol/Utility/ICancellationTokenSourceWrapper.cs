namespace Lantern.Discv5.WireProtocol.Utility;

public interface ICancellationTokenSourceWrapper
{
    CancellationToken GetToken();

    void Cancel();

    bool IsCancellationRequested();
}