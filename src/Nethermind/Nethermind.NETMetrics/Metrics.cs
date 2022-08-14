using System.ComponentModel;

namespace Nethermind.NETMetrics;

public class Metrics
{
    [Description("% Time in GC since last GC (%) ")]
    public static string TimeInGCSinceLastGC { get; set; }

    [Description("Allocation Rate (B / 1 sec)")]
    public static string AllocationRate { get; set; }

    [Description("GC Committed Bytes (MB)")]
    public static string GCCommittedBytes { get; set; }

    [Description("GC Fragmentation (%)")]
    public static string GCFragmentation { get; set; }

    [Description("GC Heap Size (MB)")]
    public static string GCHeapSize { get; set; }

    [Description("Gen 0 GC Count (Count / 1 sec)")]
    public static string Gen0GCCount { get; set; }

    [Description("Gen 0 Size (B)")]
    public static string Gen0Size { get; set; }

    [Description("Gen 1 GC Count (Count / 1 sec)")]
    public static string Gen1GCCount { get; set; }

    [Description("Gen 1 Size (B)")]
    public static string Gen1Size { get; set; }

    [Description("Gen 2 GC Count (Count / 1 sec)")]
    public static string Gen2GCCount { get; set; }

    [Description("Gen 2 Size (B)")]
    public static string Gen2Size { get; set; }

    [Description("LOH Size (B)")]
    public static string LOHSize { get; set; }

    [Description("POH (Pinned Object Heap) Size (B)")]
    public static string POHSize { get; set; }
}
