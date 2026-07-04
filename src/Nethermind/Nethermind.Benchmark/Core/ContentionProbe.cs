// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Diagnostics;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// A manual measurement pass OUTSIDE BenchmarkDotNet that answers two questions BenchmarkDotNet's per-iteration
/// isolation cannot: (1) how much CPU time each backend's own dispatch burns per batch - the cost stolen from Lane A -
/// exposing whether a GPU synchronize busy-waits on a full core; and (2) whether a co-running iGPU steals memory-bus /
/// power from the saturator threads, measured as the saturator's throughput drop while the iGPU hashes.
/// </summary>
/// <remarks>
/// Deliberately not a BenchmarkDotNet benchmark: it reads <see cref="Process.TotalProcessorTime"/> and the saturator's
/// iteration counters across a fixed invocation window, both process-wide signals that only make sense measured once,
/// end to end, not per isolated iteration. Numbers here are coarse (one clean pass, not statistically rigorous) and are
/// reported as such. Invoke via the benchmark runner with <c>--contention-probe</c>.
/// </remarks>
public static class ContentionProbe
{
    private const int N = 65536;      // one representative batch size (GPU crossover region)
    private const int Iterations = 200; // enough dispatches to get a stable CPU-time delta

    public static void Run()
    {
        Console.WriteLine("=== Keccak batch contention probe (manual, outside BenchmarkDotNet) ===");
        Console.WriteLine($"Cores: {RuntimeInformation.PhysicalCoreCount} physical / {Environment.ProcessorCount} logical");
        Console.WriteLine($"Batch N={N}, TrieMix profile, {Iterations} dispatches per measurement.");
        Console.WriteLine();

        (byte[] flat, int[] offsets) = BuildTrieMix(N);
        ValueHash256[] outputs = new ValueHash256[N];
        KeccakBatchBackend[] backends = KeccakBatchBackendCatalog.Discover();

        try
        {
            // (1) CPU time consumed per batch, idle machine. Isolates the hasher's own CPU cost (worker threads for the
            // CPU backends; the dispatch/synchronize thread for GPU backends).
            Console.WriteLine("--- (1) CPU-seconds consumed by the hasher itself, per batch (idle machine) ---");
            Console.WriteLine($"{"Backend",-62} {"wall ms/batch",14} {"cpu ms/batch",13} {"cpu/wall",9}");
            foreach (KeccakBatchBackend backend in backends)
            {
                (double wallMs, double cpuMs) = MeasureCpuCost(backend, flat, offsets, outputs);
                Console.WriteLine($"{backend.Name,-62} {wallMs,14:F3} {cpuMs,13:F3} {cpuMs / wallMs,9:F2}");
            }
            Console.WriteLine();

            // (2) Does a co-running iGPU (or CUDA) steal memory-bus/power from the saturator threads? Measure saturator
            // throughput with the machine otherwise idle, then again while each backend hashes continuously.
            Console.WriteLine("--- (2) Saturator throughput while each backend hashes continuously ---");
            int workers = Math.Max(1, RuntimeInformation.PhysicalCoreCount - 1);
            Console.WriteLine($"Saturator workers: {workers}");

            using (CpuSaturator saturator = new(workers))
            {
                double baseline = SampleSaturatorThroughput(saturator, null, flat, offsets, outputs);
                Console.WriteLine($"{"(no hasher, baseline)",-62} {baseline,14:F0} it/s {"1.00x",9}");

                foreach (KeccakBatchBackend backend in backends)
                {
                    double withHasher = SampleSaturatorThroughput(saturator, backend, flat, offsets, outputs);
                    Console.WriteLine($"{backend.Name,-62} {withHasher,14:F0} it/s {withHasher / baseline,8:F2}x");
                }
            }
            Console.WriteLine();
            Console.WriteLine("cpu/wall ~1.0 => one core busy the whole batch (CPU-bound or busy-wait sync);");
            Console.WriteLine("cpu/wall <<1.0 => the hasher thread mostly sleeps while the device works (offloaded);");
            Console.WriteLine("cpu/wall  > many => the multi-core backend used that many cores for the batch.");
            Console.WriteLine("(2) ratio <1.0 => the backend slowed the saturator (bus/power/core contention).");
        }
        finally
        {
            KeccakBatchBackendCatalog.DisposeAll(backends);
        }
    }

    // Wall time and process CPU time attributable to Iterations dispatches. CPU delta over an idle machine is the
    // hasher's own consumption; over a saturated machine it would also include the saturator, so this pass runs idle.
    private static (double wallMs, double cpuMs) MeasureCpuCost(KeccakBatchBackend backend, byte[] flat, int[] offsets, ValueHash256[] outputs)
    {
        Process self = Process.GetCurrentProcess();

        // Warm up the device/JIT so the measured window is steady state.
        for (int i = 0; i < 10; i++) backend.Hasher.HashBatch(flat, offsets, outputs);

        TimeSpan cpu0 = self.TotalProcessorTime;
        long wall0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < Iterations; i++) backend.Hasher.HashBatch(flat, offsets, outputs);
        double wallMs = Stopwatch.GetElapsedTime(wall0).TotalMilliseconds;
        double cpuMs = (self.TotalProcessorTime - cpu0).TotalMilliseconds;

        return (wallMs / Iterations, cpuMs / Iterations);
    }

    // Saturator iterations/sec over a ~2s window; if backend is non-null, one thread continuously dispatches it during
    // the window so the device runs concurrently with the saturator.
    private static double SampleSaturatorThroughput(CpuSaturator saturator, KeccakBatchBackend? backend, byte[] flat, int[] offsets, ValueHash256[] outputs)
    {
        const int windowMs = 2000;

        bool[] stop = [false]; // single-element array so the driver thread reads/writes the flag by reference
        System.Threading.Thread? driver = null;
        if (backend is not null)
        {
            driver = new System.Threading.Thread(() =>
            {
                while (!System.Threading.Volatile.Read(ref stop[0])) backend.Hasher.HashBatch(flat, offsets, outputs);
            })
            { IsBackground = true, Name = "probe-hasher" };
            driver.Start();
        }

        // Let it reach steady state, then sample.
        System.Threading.Thread.Sleep(300);
        long it0 = saturator.TotalIterations();
        long t0 = Stopwatch.GetTimestamp();
        System.Threading.Thread.Sleep(windowMs);
        double seconds = Stopwatch.GetElapsedTime(t0).TotalSeconds;
        long it1 = saturator.TotalIterations();

        if (driver is not null)
        {
            System.Threading.Volatile.Write(ref stop[0], true);
            driver.Join();
        }

        return (it1 - it0) / seconds;
    }

    private static (byte[] flat, int[] offsets) BuildTrieMix(int n)
    {
        int[] offsets = new int[n];
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            int len = (i % 3) switch { 0 => 70, 1 => 110, _ => 532 };
            total += len;
            offsets[i] = total;
        }
        byte[] flat = new byte[total];
        new Random(1).NextBytes(flat);
        return (flat, offsets);
    }
}
