using System.ComponentModel;

namespace Nethermind.NETMetrics;

public class Metrics
{
    [Description("% Time in GC since last GC (%) ")]
    public static long TimeInGcSinceLastGc { get; set; }

    [Description("The number of bytes allocated per update interval")]
    public static long AllocationRate { get; set; }

    [Description("The number of bytes committed by the GC	")]
    public static long GcCommittedBytes { get; set; }

    [Description("The GC Heap Fragmentation")]
    public static long GcFragmentation { get; set; }

    [Description("The number of megabytes thought to be allocated based on GC.GetTotalMemory(Boolean)")]
    public static long GcHeapSize { get; set; }

    [Description("The number of times GC has occurred for Gen 0 per update interval")]
    public static long Gen0GcCount { get; set; }

    [Description("The number of bytes for Gen 0 GC	")]
    public static long Gen0Size { get; set; }

    [Description("The number of times GC has occurred for Gen 1 per update interval	")]
    public static long Gen1GcCount { get; set; }

    [Description("The number of bytes for Gen 1 GC")]
    public static long Gen1Size { get; set; }

    [Description("The number of times GC has occurred for Gen 2 per update interval	")]
    public static long Gen2GcCount { get; set; }

    [Description("The number of bytes for Gen 2 GC")]
    public static long Gen2Size { get; set; }

    [Description("The number of bytes for the large object heap	")]
    public static long LohSize { get; set; }

    [Description("The number of bytes for the pinned object heap")]
    public static long PohSize { get; set; }

    [Description("The percent of the process's CPU usage relative to all of the system CPU resources")]
    public static long CpuUsage { get; set; }
}
