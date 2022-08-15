using System.ComponentModel;

namespace Nethermind.NETMetrics;

public class Metrics
{
    [Description("% Time in GC since last GC (%) ")]
    public static long TimeInGCSinceLastGC { get; set; }

    [Description("Allocation Rate (B / 1 sec)")]
    public static long AllocationRate { get; set; }

    [Description("GC Committed Bytes (MB)")]
    public static long GCCommittedBytes { get; set; }

    [Description("GC Fragmentation (%)")]
    public static long GCFragmentation { get; set; }

    [Description("GC Heap Size (MB)")]
    public static long GCHeapSize { get; set; }

    [Description("Gen 0 GC Count (Count / 1 sec)")]
    public static long Gen0GCCount { get; set; }

    [Description("Gen 0 Size (B)")]
    public static long Gen0Size { get; set; }

    [Description("Gen 1 GC Count (Count / 1 sec)")]
    public static long Gen1GCCount { get; set; }

    [Description("Gen 1 Size (B)")]
    public static long Gen1Size { get; set; }

    [Description("Gen 2 GC Count (Count / 1 sec)")]
    public static long Gen2GCCount { get; set; }

    [Description("Gen 2 Size (B)")]
    public static long Gen2Size { get; set; }

    [Description("LOH Size (B)")]
    public static long LOHSize { get; set; }

    [Description("POH (Pinned Object Heap) Size (B)")]
    public static long POHSize { get; set; }
}
