// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Android.Net;
using Android.OS;

namespace Nethermind.LightClient.Android;

internal sealed class ResourceUsage
{
    private long _elapsed;
    private long _cpu;
    private long _received = -1;
    private long _sent = -1;

    internal string Sample()
    {
        try
        {
            long elapsed = SystemClock.ElapsedRealtime();
            long cpu = Process.ElapsedCpuTime;
            long received = TrafficStats.GetUidRxBytes(Process.MyUid());
            long sent = TrafficStats.GetUidTxBytes(Process.MyUid());
            double seconds = (elapsed - _elapsed) / 1000d;
            string cpuText = _elapsed == 0 || seconds <= 0 ? "—" : $"{(cpu - _cpu) / (seconds * 10):F1}%";
            using Debug.MemoryInfo memory = new();
            Debug.GetMemoryInfo(memory);
            // TotalPss includes swapped-out pages; RAM should show only the resident proportional share.
            long ram = (long)memory.DalvikPss + memory.NativePss + memory.OtherPss;
            string text = $"RAM  {ram / 1024d:F0} MiB (PSS)  ·  CPU  {cpuText}\n" +
                $"CPU: 100% = one core\nTraffic  ↓ {Rate(received, _received, seconds)}  ↑ {Rate(sent, _sent, seconds)}\n" +
                $"Since device boot  ↓ {Total(received)}  ↑ {Total(sent)}";
            _elapsed = elapsed;
            _cpu = cpu;
            _received = received;
            _sent = sent;
            return text;
        }
        catch (Exception error)
        {
            global::Android.Util.Log.Warn("NethermindLC", $"App usage unavailable: {error.Message}");
            return "App usage unavailable";
        }
    }

    private static string Rate(long current, long previous, double seconds) =>
        previous < 0 || current < previous || seconds <= 0 ? "—" : Bytes((current - previous) / seconds) + "/s";

    private static string Total(long bytes) => bytes < 0 ? "unavailable" : Bytes(bytes);

    private static string Bytes(double bytes) => bytes < 1024 * 1024 ? $"{bytes / 1024:F1} KiB" : $"{bytes / (1024 * 1024):F1} MiB";
}
