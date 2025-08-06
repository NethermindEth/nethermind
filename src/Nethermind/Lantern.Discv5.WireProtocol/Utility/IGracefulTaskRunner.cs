namespace Lantern.Discv5.WireProtocol.Utility;

public interface IGracefulTaskRunner
{
    Task RunWithGracefulCancellationAsync(Func<CancellationToken, Task> taskFunc, string description, CancellationToken cancellationToken);
}