// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery;

// The congestion mechanism of uTP.
// see: https://datatracker.ietf.org/doc/html/rfc6817
// TODO: Parameterized these
public class LedBat
{
    // Effectively inf without having to accidentally overflow
    // TODO: Do this properly
    private static int HI = 1_000_000;
    private static uint MAX_WINDOW_SIZE = 1_000_000;


    // TODO: Dynamically discover these two value
    //Sender's Maximum Segment Size
    private static uint MSS = 500;
    private static uint INIT_CWND = 32;

    private static uint MIN_CWND = 32;

    private static uint ALLOWED_INCREASE = 2;
    //Determines the rate at which the cwnd responds to changes in queueing delay.
    private static uint GAIN = 1;

    //Maximum queueing delay(in microseconds) that LEDBAT itself may introduce in the network.
    private static uint TARGET = 100_000;

    // The, RFC mentioned 60 second though, so this should be long lived.
    private static uint BASE_DELAY_ADJ_INTERVAL = 100_000; // Micros.


    //Round-trip time
    private static int RTT = 100_000; // Just an estimate right now

    // Kinda slowstart. See https://github.com/arvidn/libtorrent/blob/9aada93c982a2f0f76b129857bec3f885a37c437/src/utp_stream.cpp#L3223
    private uint SsThres { get; set; }
    private bool IsSlowStart = true;

    private uint _lastBaseDelayAdj = 0;
    private uint _lastDataLossAdjustment = 0;

    //maintains a list of one-way delay measurements.
    private FixedRollingAvg currentDelays; //find a better name
    private static int CURRENT_FILTERS = 100;  //find a better name


    // TODO: The base delay does not need the expiry, AND should be set every BASE_DELAY_ADJ_INTERVAL, if no packet at that time, should be set to HI.
    //maintains a list of one-way delay minima over a number of one-minute intervals.
    private FixedRollingAvg baseDelays; //find a better name
    private static int BASE_HISTORY = 10; //find a better name

    // m_cwnd = amount of data that is allowed to be outstanding in a RTT. Defined in bytes.
    // private uint EffectiveWindowSize => Math.Min(_trafficControl.WindowSize, _lastPacketFromPeer?.WindowSize ?? 1024);
    public uint WindowSize { get; set; }  //find a better name

    public LedBat()
    {
        currentDelays = new FixedRollingAvg(CURRENT_FILTERS, HI, RTT);
        baseDelays = new FixedRollingAvg(BASE_HISTORY, HI, HI);
        WindowSize = INIT_CWND * MSS;

    }

    /// <param name="bytes_newly_acked"> Is the number of bytes that his ACK newly acknowledges</param>
    /// <param name="flightSize"> Is the amount of data outstanding before this ACK was received and is updated later</param>
    public void OnAck(ulong bytes_newly_acked, ulong flightSize, uint delayMicros, uint nowMicros)
    {
        // TODO: Or.. it can stay uint?
        int delayMicrosInt = (int)delayMicros;

        currentDelays.Observe(delayMicrosInt, nowMicros);
        if (_lastBaseDelayAdj / BASE_DELAY_ADJ_INTERVAL != nowMicros / BASE_DELAY_ADJ_INTERVAL)
        {
            _lastBaseDelayAdj = nowMicros;
            baseDelays.Observe(delayMicrosInt, nowMicros);
        }
        else
        {
            baseDelays.AdjustMin(delayMicrosInt, nowMicros);
        }

        int delay = currentDelays.GetAvgFixed16Precision(nowMicros);
        int baseDelay = baseDelays.GetAvgFixed16Precision(nowMicros);

        Console.Error.WriteLine($"The delays {delay} {baseDelay}");

        if (delay > baseDelay)
        {
            if (IsSlowStart)
            {
                SsThres = WindowSize / 2;
                IsSlowStart = false;
            }
        }

        bool cwndSaturated = bytes_newly_acked + flightSize + MSS > WindowSize;

        long gain = 0;
        if (cwndSaturated)
        {
            // linear gain = current-queueing delay  - predetermined TARGET delay
            long queueingDelay = currentDelays.GetAvgFixed16Precision(nowMicros) - baseDelays.GetAvgFixed16Precision(nowMicros);
            //Is a normalized value. Can be positive or negative.
            long offTarget = ((TARGET << 16) - queueingDelay) / TARGET;
            gain = GAIN * offTarget * (long)bytes_newly_acked * MSS/ (WindowSize << 16);

            long exponentialGain = (long)bytes_newly_acked;

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

    //CTO =  Congestion time out.
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
