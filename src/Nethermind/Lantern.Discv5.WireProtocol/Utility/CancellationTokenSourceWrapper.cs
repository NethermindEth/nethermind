namespace Lantern.Discv5.WireProtocol.Utility;

public sealed class CancellationTokenSourceWrapper : ICancellationTokenSourceWrapper, IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken GetToken()
    {
        return _cts.Token;
    }

    public void Cancel()
    {
        _cts.Cancel();
    }

    public bool IsCancellationRequested()
    {
        return _cts.IsCancellationRequested;
    }

    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Dispose managed resources.
            _cts.Dispose();
        }
    }

    public void Dispose()
    {
        // Dispose of managed resources.
        Dispose(true);
        // Suppress finalization.
        GC.SuppressFinalize(this);
    }
}