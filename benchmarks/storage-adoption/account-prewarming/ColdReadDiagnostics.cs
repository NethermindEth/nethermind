using System.Diagnostics;
using System.Globalization;

readonly record struct ColdReadDiagnostics(
    int Gen0, int Gen1, int Gen2, double GcPauseMs, double CpuMs,
    int Threads, long CompletedWork, long ReadBytes, long ReadCalls,
    long MinorFaults, long MajorFaults)
{
    internal static ColdReadDiagnostics Capture()
    {
        long readBytes = 0;
        long readCalls = 0;
        foreach (string line in File.ReadLines("/proc/self/io"))
        {
            if (line.StartsWith("read_bytes:", StringComparison.Ordinal)) readBytes = ParseValue(line);
            else if (line.StartsWith("syscr:", StringComparison.Ordinal)) readCalls = ParseValue(line);
        }
        string stat = File.ReadAllText("/proc/self/stat");
        string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        using Process process = Process.GetCurrentProcess();
        return new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            GC.GetTotalPauseDuration().TotalMilliseconds, process.TotalProcessorTime.TotalMilliseconds,
            ThreadPool.ThreadCount, ThreadPool.CompletedWorkItemCount, readBytes, readCalls,
            long.Parse(fields[7], CultureInfo.InvariantCulture), long.Parse(fields[9], CultureInfo.InvariantCulture));
    }

    internal object Since(ColdReadDiagnostics before) => new
    {
        gen0Collections = Gen0 - before.Gen0,
        gen1Collections = Gen1 - before.Gen1,
        gen2Collections = Gen2 - before.Gen2,
        gcPauseMs = GcPauseMs - before.GcPauseMs,
        processCpuMs = CpuMs - before.CpuMs,
        threadPoolThreadsBefore = before.Threads,
        threadPoolThreadsAfter = Threads,
        completedWorkItems = CompletedWork - before.CompletedWork,
        readBytes = ReadBytes - before.ReadBytes,
        readCalls = ReadCalls - before.ReadCalls,
        minorFaults = MinorFaults - before.MinorFaults,
        majorFaults = MajorFaults - before.MajorFaults
    };

    private static long ParseValue(string line) => long.Parse(line.AsSpan(line.IndexOf(':') + 1), CultureInfo.InvariantCulture);
}
