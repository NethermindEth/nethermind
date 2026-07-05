// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ILGPU;
using ILGPU.Runtime;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Manual measurement pass (OUTSIDE BenchmarkDotNet, invoked via <c>--gpu-transfer-probe</c>) that replaces the
/// ASSUMED GPU transfer/dispatch constants used elsewhere in the benchmark tree (a fixed ~5-20us/copy latency,
/// a ~0.5MB latency/bandwidth breakeven, ~20 GB/s effective bandwidth, and a ~300us/dispatch floor) with values
/// MEASURED on this host, per device. It measures each non-CPU accelerator ILGPU can reach - here the discrete
/// CUDA card and the integrated OpenCL iGPU - because the iGPU shares system DRAM and may have a radically
/// different copy cost structure from a discrete card behind PCIe.
/// </summary>
/// <remarks>
/// Deliberately not a BenchmarkDotNet benchmark: it drives raw ILGPU MemoryBuffer / AcceleratorStream primitives
/// (page-locked staging, single-stream synchronous copies, empty-kernel launches) and reports the median of a
/// large repetition count after warmup. BenchmarkDotNet's per-iteration process isolation would obscure the
/// steady-state device+driver state these numbers depend on. Numbers are single-pass medians, reported as such.
/// <para>
/// Everything here is benchmark-local. The probe creates its own ILGPU context/accelerator (same enumeration
/// pattern as <c>GpuKeccakBatchHasher</c>) rather than going through the hasher, because it needs the raw
/// buffer/stream surface the hasher does not expose. Never throws out: a device that fails to initialize is
/// skipped with a printed note.
/// </para>
/// </remarks>
public static class GpuTransferProbe
{
    private const int LatencyReps = 200;       // >= 100 required; median over this many round trips
    private const int BandwidthReps = 100;     // per size, per direction
    private const int DispatchReps = 200;      // empty-kernel launch+sync round trips
    private const int WarmupReps = 30;

    // Bandwidth-curve sizes in bytes.
    private static readonly (string Label, int Bytes)[] BandwidthSizes =
    [
        ("4KB", 4 * 1024),
        ("64KB", 64 * 1024),
        ("256KB", 256 * 1024),
        ("1MB", 1024 * 1024),
        ("4MB", 4 * 1024 * 1024),
        ("16MB", 16 * 1024 * 1024),
        ("64MB", 64 * 1024 * 1024),
    ];

    // Tiny-copy sizes for the fixed-latency measurement.
    private static readonly (string Label, int Bytes)[] TinySizes =
    [
        ("64B", 64),
        ("1KB", 1024),
    ];

    // Batching-equivalence cases: N small copies vs 1 large copy of the same total bytes (H2D pinned).
    private static readonly (int Count, int SmallBytes, string OneLargeLabel)[] BatchingCases =
    [
        (64, 4 * 1024, "256KB"),
        (256, 4 * 1024, "1MB"),
        (1024, 4 * 1024, "4MB"),
    ];

    // Dispatch-decomposition kernel payload (in and out), matching the smallest "interesting" batch transfer.
    private const int DispatchPayloadBytes = 256 * 1024;

    public static void Run()
    {
        Console.WriteLine("=== GPU transfer / dispatch cost probe (manual, outside BenchmarkDotNet) ===");
        Console.WriteLine("Purpose: measure real per-copy latency, bandwidth curve + breakeven, many-small-vs-one-large");
        Console.WriteLine("penalty, and dispatch decomposition, per device, to replace assumed constants.");
        Console.WriteLine();
        Console.WriteLine("Methodology:");
        Console.WriteLine("  - Raw ILGPU MemoryBuffer1D + a single AcceleratorStream; each measured op is timed then");
        Console.WriteLine("    stream.Synchronize() so wall time includes the full submit->device->complete round trip.");
        Console.WriteLine("  - Pinned host memory = ILGPU AllocatePageLocked1D; regular = a plain managed byte[].");
        Console.WriteLine("  - Latency: tiny copies (64B, 1KB), H2D and D2H separately, pinned vs regular, median of");
        Console.WriteLine($"    {LatencyReps} reps after {WarmupReps} warmup reps.");
        Console.WriteLine($"  - Bandwidth: one-way copy per size, pinned staging, both directions, median of {BandwidthReps} reps.");
        Console.WriteLine("    Effective GB/s = bytes / median-seconds. Breakeven = smallest size whose one-way copy time");
        Console.WriteLine("    exceeds 2x the measured fixed tiny-copy latency (i.e. the bandwidth term at least matches latency).");
        Console.WriteLine("  - Batching: N x small vs 1 x (N*small) bytes, H2D pinned; ratio = N-small time / one-large time.");
        Console.WriteLine($"  - Dispatch: empty-kernel launch+sync round trip (no data, median of {DispatchReps}); then the same");
        Console.WriteLine($"    kernel with {DispatchPayloadBytes / 1024}KB in + {DispatchPayloadBytes / 1024}KB out to compose launch/sync vs transfer vs kernel.");
        Console.WriteLine("  - Timings are single-pass medians (Stopwatch), not statistically rigorous - treat as ~order-of-magnitude.");
        Console.WriteLine();

        IReadOnlyList<DeviceEntry> devices = EnumerateProbeDevices();
        if (devices.Count == 0)
        {
            Console.WriteLine("No non-CPU accelerator found (or ILGPU context creation failed). Nothing to measure.");
            return;
        }

        Console.WriteLine($"Devices found: {devices.Count}");
        foreach (DeviceEntry d in devices)
        {
            Console.WriteLine($"  [{d.Index}] {d.Type,-8} {d.Name}  ({d.MemoryBytes / (1024L * 1024L)} MB)");
        }
        Console.WriteLine();

        foreach (DeviceEntry d in devices)
        {
            MeasureDevice(d);
        }
    }

    private readonly record struct DeviceEntry(int Index, string Name, AcceleratorType Type, long MemoryBytes);

    private static IReadOnlyList<DeviceEntry> EnumerateProbeDevices()
    {
        Context? context = null;
        try
        {
            context = Context.Create(builder => builder.Default().AllAccelerators());
            List<DeviceEntry> devices = [];
            int index = 0;
            foreach (Device device in context.Devices)
            {
                if (device.AcceleratorType == AcceleratorType.CPU) continue;
                devices.Add(new DeviceEntry(index++, device.Name, device.AcceleratorType, device.MemorySize));
            }
            return devices;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Device enumeration failed: {e.GetType().Name}: {e.Message}");
            return [];
        }
        finally
        {
            context?.Dispose();
        }
    }

    private static void MeasureDevice(DeviceEntry entry)
    {
        Console.WriteLine("=========================================================================================");
        Console.WriteLine($"DEVICE [{entry.Index}] {entry.Type} - {entry.Name}");
        Console.WriteLine("=========================================================================================");

        Context? context = null;
        Accelerator? accelerator = null;
        try
        {
            context = Context.Create(builder => builder.Default().AllAccelerators());
            int index = 0;
            Device? target = null;
            foreach (Device device in context.Devices)
            {
                if (device.AcceleratorType == AcceleratorType.CPU) continue;
                if (index++ == entry.Index) { target = device; break; }
            }
            if (target is null)
            {
                Console.WriteLine("  Device disappeared between enumeration and measurement; skipping.");
                return;
            }

            accelerator = target.CreateAccelerator(context);
            using AcceleratorStream stream = accelerator.CreateStream();

            MeasureLatency(accelerator, stream);
            double breakevenBandwidthPinnedH2dGiBs = MeasureBandwidth(accelerator, stream, out double tinyH2dLatencyUs);
            MeasureBatching(accelerator, stream);
            MeasureDispatch(accelerator, stream, entry.Type);
            MeasureZeroCopyNote(entry, tinyH2dLatencyUs, breakevenBandwidthPinnedH2dGiBs);
        }
        catch (Exception e)
        {
            Console.WriteLine($"  Device init/measurement failed: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            accelerator?.Dispose();
            context?.Dispose();
        }
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------------------------------------------
    // (1) Fixed per-copy latency: tiny copies, H2D and D2H, pinned vs regular.
    // -------------------------------------------------------------------------------------------------------------
    private static void MeasureLatency(Accelerator accelerator, AcceleratorStream stream)
    {
        Console.WriteLine("--- (1) Fixed per-copy latency: tiny copies, one-way, median us ---");
        Console.WriteLine($"{"size",6} {"H2D pinned",12} {"H2D regular",12} {"D2H pinned",12} {"D2H regular",12}");

        foreach ((string label, int bytes) in TinySizes)
        {
            using MemoryBuffer1D<byte, Stride1D.Dense> dev = accelerator.Allocate1D<byte>(bytes);
            using PageLockedArray1D<byte> pinned = accelerator.AllocatePageLocked1D<byte>(bytes);
            byte[] regular = new byte[bytes];

            double h2dPinned = MedianUs(LatencyReps, WarmupReps, () => CopyH2dPinned(dev, pinned, stream, bytes));
            double h2dRegular = MedianUs(LatencyReps, WarmupReps, () => CopyH2dRegular(dev, regular, stream, bytes));
            double d2hPinned = MedianUs(LatencyReps, WarmupReps, () => CopyD2hPinned(dev, pinned, stream, bytes));
            double d2hRegular = MedianUs(LatencyReps, WarmupReps, () => CopyD2hRegular(dev, regular, stream, bytes));

            Console.WriteLine($"{label,6} {h2dPinned,12:F2} {h2dRegular,12:F2} {d2hPinned,12:F2} {d2hRegular,12:F2}");
        }
        Console.WriteLine("  One-way = one CopyFrom/ToCPU + stream.Synchronize(). This is the fixed floor per transfer.");
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------------------------------------------
    // (2) Bandwidth curve + breakeven. Returns pinned-H2D GB/s at the largest size (asymptote) and, via out,
    // the tiny (64B) pinned-H2D latency used as the fixed-latency reference for the breakeven search.
    // -------------------------------------------------------------------------------------------------------------
    private static double MeasureBandwidth(Accelerator accelerator, AcceleratorStream stream, out double tinyH2dLatencyUs)
    {
        Console.WriteLine("--- (2) Bandwidth curve: one-way copy, pinned staging, median us and effective GB/s ---");
        Console.WriteLine($"{"size",8} {"H2D us",10} {"H2D GB/s",10} {"D2H us",10} {"D2H GB/s",10}");

        // Fixed-latency reference: the 64B pinned H2D one-way time (smallest transfer, all overhead, ~no bandwidth term).
        using (MemoryBuffer1D<byte, Stride1D.Dense> tinyDev = accelerator.Allocate1D<byte>(64))
        using (PageLockedArray1D<byte> tinyPinned = accelerator.AllocatePageLocked1D<byte>(64))
        {
            tinyH2dLatencyUs = MedianUs(LatencyReps, WarmupReps, () => CopyH2dPinned(tinyDev, tinyPinned, stream, 64));
        }

        double asymptoteH2dGiBs = 0;
        double h2dBreakevenUs = -1;
        string breakevenLabel = "(none)";
        foreach ((string label, int bytes) in BandwidthSizes)
        {
            using MemoryBuffer1D<byte, Stride1D.Dense> dev = accelerator.Allocate1D<byte>(bytes);
            using PageLockedArray1D<byte> pinned = accelerator.AllocatePageLocked1D<byte>(bytes);

            double h2dUs = MedianUs(BandwidthReps, WarmupReps, () => CopyH2dPinned(dev, pinned, stream, bytes));
            double d2hUs = MedianUs(BandwidthReps, WarmupReps, () => CopyD2hPinned(dev, pinned, stream, bytes));

            double h2dGiBs = bytes / (h2dUs * 1e-6) / 1e9;
            double d2hGiBs = bytes / (d2hUs * 1e-6) / 1e9;
            asymptoteH2dGiBs = h2dGiBs; // last (largest) size wins => asymptotic bandwidth

            if (h2dBreakevenUs < 0 && h2dUs > 2.0 * tinyH2dLatencyUs)
            {
                h2dBreakevenUs = h2dUs;
                breakevenLabel = label;
            }

            Console.WriteLine($"{label,8} {h2dUs,10:F2} {h2dGiBs,10:F2} {d2hUs,10:F2} {d2hGiBs,10:F2}");
        }
        Console.WriteLine($"  Fixed-latency reference (64B pinned H2D one-way): {tinyH2dLatencyUs:F2} us.");
        Console.WriteLine($"  Breakeven (smallest size whose H2D copy time > 2x the fixed latency, i.e. bandwidth-dominated): {breakevenLabel}.");
        Console.WriteLine("  GB/s = decimal (10^9). Effective GB/s rises with size as the fixed latency amortizes.");
        Console.WriteLine();
        return asymptoteH2dGiBs;
    }

    // -------------------------------------------------------------------------------------------------------------
    // (3) Batching equivalence: N small copies vs 1 large copy of the same total bytes, H2D pinned.
    // -------------------------------------------------------------------------------------------------------------
    private static void MeasureBatching(Accelerator accelerator, AcceleratorStream stream)
    {
        Console.WriteLine("--- (3) Batching: N small H2D copies vs 1 large H2D copy, same total bytes, pinned, median us ---");
        Console.WriteLine($"{"case",22} {"N-small us",12} {"1-large us",12} {"ratio",8}");

        foreach ((int count, int smallBytes, string oneLargeLabel) in BatchingCases)
        {
            int totalBytes = count * smallBytes;
            using MemoryBuffer1D<byte, Stride1D.Dense> dev = accelerator.Allocate1D<byte>(totalBytes);
            using PageLockedArray1D<byte> pinned = accelerator.AllocatePageLocked1D<byte>(totalBytes);

            // N small copies: each writes into a distinct device sub-range so it is genuinely N transfers.
            double nSmallUs = MedianUs(BandwidthReps, WarmupReps, () =>
            {
                for (int i = 0; i < count; i++)
                {
                    ArrayView1D<byte, Stride1D.Dense> sub = dev.View.SubView((long)i * smallBytes, smallBytes);
                    sub.CopyFromCPU(stream, ref pinned.Span[i * smallBytes], smallBytes);
                }
                stream.Synchronize();
            });

            // 1 large copy of the same total bytes.
            double oneLargeUs = MedianUs(BandwidthReps, WarmupReps, () => CopyH2dPinned(dev, pinned, stream, totalBytes));

            double ratio = oneLargeUs > 0 ? nSmallUs / oneLargeUs : 0;
            string caseLabel = $"{count}x4KB vs 1x{oneLargeLabel}";
            Console.WriteLine($"{caseLabel,22} {nSmallUs,12:F2} {oneLargeUs,12:F2} {ratio,8:F2}");
        }
        Console.WriteLine("  ratio = (N small copies time) / (one large copy time). >1 => many-small pays a per-copy launch tax.");
        Console.WriteLine("  N small copies here are submitted on ONE stream then synchronized once (per-copy submit cost, one sync).");
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------------------------------------------
    // (4) Dispatch decomposition: empty kernel launch+sync (no data) vs kernel + 256KB in + 256KB out.
    // -------------------------------------------------------------------------------------------------------------
    private static void MeasureDispatch(Accelerator accelerator, AcceleratorStream stream, AcceleratorType type)
    {
        Console.WriteLine("--- (4) Dispatch decomposition: median us ---");

        // A trivial kernel: one output byte incremented. Kept non-empty so the compiler/driver cannot elide the launch.
        // LoadAutoGroupedKernel (not the *Stream* variant) returns a launcher whose first parameter is the stream,
        // so the launch and its data copies are timed and synchronized on the same explicit stream.
        Action<AcceleratorStream, Index1D, ArrayView<byte>, ArrayView<byte>> kernel =
            accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<byte>, ArrayView<byte>>(TouchKernel);

        // (4a) Empty launch: 1-thread kernel over 1-byte buffers, launch + sync, NO host<->device data transfer.
        using MemoryBuffer1D<byte, Stride1D.Dense> tinyIn = accelerator.Allocate1D<byte>(1);
        using MemoryBuffer1D<byte, Stride1D.Dense> tinyOut = accelerator.Allocate1D<byte>(1);
        double emptyLaunchUs = MedianUs(DispatchReps, WarmupReps, () =>
        {
            kernel(stream, 1, tinyIn.View, tinyOut.View);
            stream.Synchronize();
        });

        // (4b) Kernel + payload: copy 256KB H2D, launch over all elements, copy 256KB D2H, one sync.
        int n = DispatchPayloadBytes;
        using MemoryBuffer1D<byte, Stride1D.Dense> inBuf = accelerator.Allocate1D<byte>(n);
        using MemoryBuffer1D<byte, Stride1D.Dense> outBuf = accelerator.Allocate1D<byte>(n);
        using PageLockedArray1D<byte> hostIn = accelerator.AllocatePageLocked1D<byte>(n);
        using PageLockedArray1D<byte> hostOut = accelerator.AllocatePageLocked1D<byte>(n);

        double kernelPlusCopyUs = MedianUs(DispatchReps, WarmupReps, () =>
        {
            inBuf.View.CopyFromCPU(stream, ref hostIn.Span[0], n);
            kernel(stream, n, inBuf.View, outBuf.View);
            outBuf.View.CopyToCPU(stream, ref hostOut.Span[0], n);
            stream.Synchronize();
        });

        // Pure copy references (already have pinned bandwidth in section 2, but re-measure here so the decomposition
        // is self-contained and uses the same stream/buffers state).
        double h2d256Us = MedianUs(DispatchReps, WarmupReps, () => CopyH2dPinnedBuf(inBuf, hostIn, stream, n));
        double d2h256Us = MedianUs(DispatchReps, WarmupReps, () => CopyD2hPinnedBuf(outBuf, hostOut, stream, n));

        Console.WriteLine($"  empty-kernel launch+sync (no data)          : {emptyLaunchUs,10:F2} us");
        Console.WriteLine($"  H2D 256KB pinned (one-way)                  : {h2d256Us,10:F2} us");
        Console.WriteLine($"  D2H 256KB pinned (one-way)                  : {d2h256Us,10:F2} us");
        Console.WriteLine($"  kernel + 256KB in + 256KB out (+1 sync)     : {kernelPlusCopyUs,10:F2} us");
        double kernelResidualUs = kernelPlusCopyUs - h2d256Us - d2h256Us - emptyLaunchUs;
        Console.WriteLine($"  => kernel-compute residual (full - H2D - D2H - launch) : {kernelResidualUs,10:F2} us");
        Console.WriteLine("  Decomposition: full round trip = launch/sync floor + H2D + D2H + kernel compute (+ overlap/error).");
        Console.WriteLine("  A negative residual means the copies overlap the launch floor on this device (sync is not additive).");
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------------------------------------------
    // (5) iGPU zero-copy note.
    // -------------------------------------------------------------------------------------------------------------
    private static void MeasureZeroCopyNote(DeviceEntry entry, double tinyH2dLatencyUs, double asymptoteH2dGiBs)
    {
        Console.WriteLine("--- (5) Shared-DRAM / zero-copy note ---");
        if (entry.Type == AcceleratorType.OpenCL)
        {
            Console.WriteLine("  This is an OpenCL device. On an integrated GPU it shares system DRAM with the CPU, so the");
            Console.WriteLine("  CopyFromCPU/CopyToCPU path is a DRAM->DRAM memcpy (plus driver marshalling), NOT a PCIe transfer.");
            Console.WriteLine($"  Observed: fixed tiny-copy latency {tinyH2dLatencyUs:F2} us, asymptotic H2D {asymptoteH2dGiBs:F1} GB/s.");
            Console.WriteLine("  ILGPU's standard MemoryBuffer path still performs an explicit copy into a device-allocated buffer;");
            Console.WriteLine("  it does not automatically alias host memory. If the copy latency/bandwidth here is close to a plain");
            Console.WriteLine("  host memcpy, a true zero-copy (host-visible / mapped) buffer would remove the copy entirely - but");
            Console.WriteLine("  that requires the OpenCL host-visible allocation path, not exercised by the standard CopyFromCPU used");
            Console.WriteLine("  by GpuKeccakBatchHasher. Implication: on a shared-DRAM device, staging may be pure overhead.");
        }
        else
        {
            Console.WriteLine($"  This is a {entry.Type} device (discrete). The CopyFromCPU/CopyToCPU path is a real PCIe DMA transfer;");
            Console.WriteLine("  staging into pinned (page-locked) host memory is what enables DMA and is worth its cost. Zero-copy");
            Console.WriteLine("  host-visible aliasing is not the right model here - the numbers above are the true transfer cost.");
        }
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------------------------------------------
    // Kernel + copy primitives.
    // -------------------------------------------------------------------------------------------------------------

    /// <summary>Trivial kernel: thread 0 copies one byte in->out; other threads no-op. Non-empty so the launch stands.</summary>
    private static void TouchKernel(Index1D index, ArrayView<byte> input, ArrayView<byte> output)
    {
        if (index < output.Length && index < input.Length)
        {
            output[index] = (byte)(input[index] + 1);
        }
    }

    private static void CopyH2dPinned(MemoryBuffer1D<byte, Stride1D.Dense> dev, PageLockedArray1D<byte> pinned, AcceleratorStream stream, int bytes)
    {
        dev.View.SubView(0, bytes).CopyFromCPU(stream, ref pinned.Span[0], bytes);
        stream.Synchronize();
    }

    private static void CopyH2dPinnedBuf(MemoryBuffer1D<byte, Stride1D.Dense> dev, PageLockedArray1D<byte> pinned, AcceleratorStream stream, int bytes)
    {
        dev.View.CopyFromCPU(stream, ref pinned.Span[0], bytes);
        stream.Synchronize();
    }

    private static void CopyH2dRegular(MemoryBuffer1D<byte, Stride1D.Dense> dev, byte[] regular, AcceleratorStream stream, int bytes)
    {
        dev.View.SubView(0, bytes).CopyFromCPU(stream, ref MemoryMarshal.GetArrayDataReference(regular), bytes);
        stream.Synchronize();
    }

    private static void CopyD2hPinned(MemoryBuffer1D<byte, Stride1D.Dense> dev, PageLockedArray1D<byte> pinned, AcceleratorStream stream, int bytes)
    {
        dev.View.SubView(0, bytes).CopyToCPU(stream, ref pinned.Span[0], bytes);
        stream.Synchronize();
    }

    private static void CopyD2hPinnedBuf(MemoryBuffer1D<byte, Stride1D.Dense> dev, PageLockedArray1D<byte> pinned, AcceleratorStream stream, int bytes)
    {
        dev.View.CopyToCPU(stream, ref pinned.Span[0], bytes);
        stream.Synchronize();
    }

    private static void CopyD2hRegular(MemoryBuffer1D<byte, Stride1D.Dense> dev, byte[] regular, AcceleratorStream stream, int bytes)
    {
        dev.View.SubView(0, bytes).CopyToCPU(stream, ref MemoryMarshal.GetArrayDataReference(regular), bytes);
        stream.Synchronize();
    }

    // -------------------------------------------------------------------------------------------------------------
    // Timing helper: median microseconds over reps, after warmup.
    // -------------------------------------------------------------------------------------------------------------
    private static double MedianUs(int reps, int warmup, Action op)
    {
        for (int i = 0; i < warmup; i++) op();

        double[] samples = new double[reps];
        for (int i = 0; i < reps; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            op();
            samples[i] = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        }
        Array.Sort(samples);
        return samples[reps / 2];
    }
}
