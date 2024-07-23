// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery;

// The congestion mechanism of uTP.
// see: https://datatracker.ietf.org/doc/html/rfc6817
// TODO: Parameterized these
// TODO: Double check everything
public class LedBat
{
    // Effectively inf without having to accidentally overflow
    // TODO: Do this properly
    private static int HI = 1_000_000;
    private static uint MAX_WINDOW_SIZE = 1_000_000;

    private static int BASE_HISTORY = 10;
    private static int CURRENT_FILTERS = 100;

    // Screw it, I'll just set a large value as minimum
    private static uint INIT_CWND = 32;
    private static uint MIN_CWND = 32;

    private static uint ALLOWED_INCREASE = 2;
    private static uint GAIN = 1;

    private static uint TARGET = 100_000; // Micros

    // The, RFC mentioned 60 second though, so this should be long lived.
    private static uint BASE_DELAY_ADJ_INTERVAL = 100_000; // Micros.

    // TODO: Dynamically discover these two value
    private static uint MSS = 500;
    private static int RTT = 100_000; // Just an estimate right now

    private FixedRollingAvg CurrentDelays = new FixedRollingAvg(CURRENT_FILTERS, HI, RTT);

    // TODO: The base delay does not need the expiry, AND should be set every BASE_DELAY_ADJ_INTERVAL, if no packet at that time, should be set to HI.
    private FixedRollingAvg BaseDelays = new FixedRollingAvg(BASE_HISTORY, HI, HI);

    private uint _lastBaseDelayAdj = 0;
    private uint _lastDataLossAdjustment = 0;

    // m_cwnd
    // private uint EffectiveWindowSize => Math.Min(_trafficControl.WindowSize, _lastPacketFromPeer?.WindowSize ?? 1024);
    public uint WindowSize { get; set; } = INIT_CWND * MSS;

    // Kinda slowstart. See https://github.com/arvidn/libtorrent/blob/9aada93c982a2f0f76b129857bec3f885a37c437/src/utp_stream.cpp#L3223
    private uint SsThres { get; set; }
    private bool IsSlowStart = true;

    public void OnAck(ulong ackedBytes, ulong flightSize, uint delayMicros, uint nowMicros)
    {
        // TODO: Or.. it can stay uint?
        int delayMicrosInt = (int)delayMicros;

        CurrentDelays.Observe(delayMicrosInt, nowMicros);
        if (_lastBaseDelayAdj / BASE_DELAY_ADJ_INTERVAL != nowMicros / BASE_DELAY_ADJ_INTERVAL)
        {
            _lastBaseDelayAdj = nowMicros;
            BaseDelays.Observe(delayMicrosInt, nowMicros);
        }
        else
        {
            BaseDelays.AdjustMin(delayMicrosInt, nowMicros);
        }

        int delay = CurrentDelays.GetAvgFixed16Precision(nowMicros);
        int baseDelay = BaseDelays.GetAvgFixed16Precision(nowMicros);

        Console.Error.WriteLine($"The delays {delay} {baseDelay}");

        if (delay > baseDelay)
        {
            if (IsSlowStart)
            {
                SsThres = WindowSize / 2;
                IsSlowStart = false;
            }
        }

        bool cwndSaturated = ackedBytes + flightSize + MSS > WindowSize;

        long gain = 0;
        if (cwndSaturated)
        {
            // linear gain
            long queueingDelay = CurrentDelays.GetAvgFixed16Precision(nowMicros) - BaseDelays.GetAvgFixed16Precision(nowMicros);
            long offTarget = ((TARGET << 16) - queueingDelay) / TARGET;
            gain = GAIN * offTarget * (long)ackedBytes * MSS/ (WindowSize << 16);

            long exponentialGain = (long)ackedBytes;

            if (IsSlowStart)
            {
                if (SsThres != 0 && WindowSize + exponentialGain > SsThres)
                {
                    IsSlowStart = false;
                }
                else
                {
                    gain = Math.Max(exponentialGain, gain);
                }
            }
        }
        else
        {
            gain = 0;
        }

        uint newWindow = (uint)(WindowSize + gain);
        uint maxAllowedCwnd = (uint)(flightSize + ALLOWED_INCREASE * MSS);

        newWindow = Math.Min(newWindow, maxAllowedCwnd);
        newWindow = Math.Max(newWindow, MIN_CWND * MSS);
        newWindow = Math.Min(newWindow, MAX_WINDOW_SIZE);

        Console.Error.WriteLine($"TC Window adjusted {WindowSize} -> {newWindow}, {IsSlowStart}, {cwndSaturated}");
        WindowSize = newWindow;

        UpdateCTO();
    }

    private void UpdateCTO()
    {
        // TODO:
        //  implements an RTT estimation mechanism using data
        //  transmission times and ACK reception times,
        //  which is used to implement a congestion timeout (CTO).
    }

    public void OnDataLoss(uint nowMicros)

    {
        Console.Error.WriteLine($"TC Data loss");
        if (_lastDataLossAdjustment / RTT != nowMicros / RTT)
        {
            _lastDataLossAdjustment = nowMicros;
            // TODO: At most once per RTT
            WindowSize = (uint)Math.Max(WindowSize / 2, MIN_CWND * MSS);
        }

        SsThres = WindowSize;
        IsSlowStart = false;
    }

}
