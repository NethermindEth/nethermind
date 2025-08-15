namespace Lantern.Discv5.WireProtocol.Table;

public class TableOptions
{
    public TableOptions(string[] bootstrapEnrs)
    {
        BootstrapEnrs = bootstrapEnrs;
    }

    public int PingIntervalMilliseconds { get; set; } = 5000;
    public int RefreshIntervalMilliseconds { get; set; } = 300000;
    public int LookupTimeoutMilliseconds { get; set; } = 10000;
    public int MaxAllowedFailures { get; set; } = 3;
    public int ReplacementCacheSize { get; set; } = 200;
    public int ConcurrencyParameter { get; set; } = 3;
    public int MaxNodesCount { get; set; } = 16;
    public int LookupParallelism { get; set; } = 2;
    public string[] BootstrapEnrs { get; set; } = Array.Empty<string>();

    public static TableOptions Default => new([]);
}