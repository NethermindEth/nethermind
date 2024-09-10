// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;

namespace Nethermind.Network.Discovery;

// The congestion mechanism of uTP.
// see: https://datatracker.ietf.org/doc/html/rfc6817
public class LEDBAT
{
    private const uint MAX_WINDOW_SIZE = 1_000_000;
    private const uint MSS = 500;   //Sender's Maximum Segment Size
    private const uint INIT_CWND = 32;
    private const uint MIN_CWND = 32;
    private const uint ALLOWED_INCREASE = 2;
    private const uint GAIN = 1; //Determines the rate at which the cwnd responds to changes in queueing delay.
    private const uint TARGET = 100_000;   //Maximum queueing delay(in microseconds) that LEDBAT itself may introduce in the network.
    private const int RTT = 100_000; // //Round-trip time: Just an estimate right now
    private const float RTTTAlpha = 0.875f;
    private const uint BASE_DELAY_ADJ_INTERVAL = 100_000; // Micros.

    private int CTO;   //Congestion time out
    private int ESTIMATED_RTT;
    private uint SsThres;  // Ref https://github.com/arvidn/libtorrent/blob/9aada93c982a2f0f76b129857bec3f885a37c437/src/utp_stream.cpp#L3223
    private bool IsSlowStart;

    private FixedRollingAvg currentDelays;  //maintains a list of one-way delay measurements.
    private FixedRollingAvg baseDelays; //maintains a list of one-way delay minima over a number of one-minute intervals.
    private uint _lastBaseDelayAdj = 0;
    private uint _lastDataLossAdjustment = 0;

    private readonly ILogger _logger;

    public uint WindowSize { get; private set; }// m_cwnd = amount of data that is allowed to be outstanding in a RTT. Defined in bytes.
    public LEDBAT(bool isSlowStart, uint initialWindowSize, ILogManager logManager)
    {
        currentDelays = new FixedRollingAvg(100, 1_000_000, RTT);
        baseDelays = new FixedRollingAvg(10, 1_000_000, 1_000_000);
        WindowSize = initialWindowSize;
        CTO = RTT * 2; // or 1 second
        IsSlowStart = isSlowStart;

        _logger = logManager.GetClassLogger<LEDBAT>();
    }

    public LEDBAT(ILogManager logManager) : this(true, INIT_CWND * MSS, logManager)
    {

    }

    /// <param name="bytes_newly_acked"> Is the number of bytes that this ACK newly acknowledges</param>
    /// <param name="flightSize"> Is the amount of data outstanding before this ACK was received and is updated later</param>
    /// <param name="timestamp_difference_microseconds"> Is the difference between the local time and the timestamp in the last received packet, at the time the last packet was received.</param>
    public void OnAck(ulong bytes_newly_acked, ulong flightSize, uint timestamp_difference_microseconds, uint nowMicros)
    {
        currentDelays.Observe((int)timestamp_difference_microseconds, nowMicros);
        updatBaseDelay(nowMicros, (int)timestamp_difference_microseconds);

        int delay = currentDelays.GetAvgFixed16Precision(nowMicros);
        int baseDelay = baseDelays.GetAvgFixed16Precision(nowMicros);

        if (_logger.IsTrace) _logger.Trace($"Current delay {delay} , Base delay: {baseDelay}");

        if (delay > baseDelay && IsSlowStart)
        {
            SsThres = WindowSize / 2; //moving from exponential to linear growth
            IsSlowStart = false;
        }

        bool cwndSaturated = bytes_newly_acked + flightSize + MSS > WindowSize;

        long gain = 0;
        if (cwndSaturated) // Calculate gain if the congestion window is saturated
        {
            // linear gain = current-queueing delay  - predetermined TARGET delay
            long queueingDelay = delay - baseDelay;
            //Is a normalized value. Can be positive or negative.
            long offTarget = ((TARGET << 16) - queueingDelay) / TARGET << 16;
            //Represents the increase (or decrease) in window size based on the network conditions, such as delays and acknowledgments
            gain = GAIN * offTarget * (long)bytes_newly_acked * (MSS << 16) / (WindowSize << 16);
            if (IsSlowStart)
            {
                long exponentialGain = (long)bytes_newly_acked;

                if (SsThres > 0 &&
                    WindowSize + exponentialGain >
                    SsThres) // has grown large enough to trigger the transition from slow start to the congestion avoidance phase.
                {
                    IsSlowStart =
                        false; // determines the point at which the congestion control algorithm should transition from the slow start phase to the congestion avoidance phase
                }
                else
                {
                    gain = Math.Max(exponentialGain, gain);
                }
            }
        }

        uint newWindow = (uint)(WindowSize + gain);
        uint maxAllowedCwnd =
            (uint)(flightSize +
                   ALLOWED_INCREASE * MSS); // does not grow too large compared to the amount of data in flight.

        newWindow = Math.Min(newWindow,
            maxAllowedCwnd); //Should not exceed maxAllowedCwnd. Prevents the window from growing too quickly or becoming excessively large
        newWindow = Math.Max(newWindow,
            MIN_CWND * MSS); //Does not fall below the minimum congestion window size. MIN_CWND * MSS converts this minimum window size to bytes.
        newWindow = Math.Min(newWindow, MAX_WINDOW_SIZE); //Does not exceed the maximum congestion window size.

        if (_logger.IsTrace) _logger.Trace($"TC Window adjusted {WindowSize} -> {newWindow}, {IsSlowStart}, {cwndSaturated}");
        WindowSize = newWindow;

        UpdateCTO();
    }

    private void updatBaseDelay(uint nowMicros, int delayMicrosInt)
    {
        if (_lastBaseDelayAdj / BASE_DELAY_ADJ_INTERVAL != nowMicros / BASE_DELAY_ADJ_INTERVAL)
        {
            _lastBaseDelayAdj = nowMicros;
            baseDelays.Observe(delayMicrosInt, nowMicros);
        }
        else
        {
            baseDelays.AdjustMin(delayMicrosInt, nowMicros);
        }
    }


    private void UpdateCTO()
    {
        int currentDelay = currentDelays.GetAvgFixed16Precision(UTPUtil.GetTimestamp());
        ESTIMATED_RTT = (int)RTTTAlpha * ESTIMATED_RTT + (1 - (int)RTTTAlpha) * currentDelay;
        CTO = ESTIMATED_RTT * 2;
    }

    public void OnDataLoss(uint nowMicros)
    {
        if (_logger.IsTrace) _logger.Trace($"TC Data loss");
        if (_lastDataLossAdjustment / RTT != nowMicros / RTT)
        {
            _lastDataLossAdjustment = nowMicros;
            // TODO: At most once per RTT
            WindowSize = (uint)Math.Max(WindowSize / 2, MIN_CWND * MSS);
        }

        SsThres = WindowSize;
        IsSlowStart = false;
    }

    public uint getMIN_CWND()
    {
        return MIN_CWND;
    }

    public uint getMSS()
    {
        return MSS;
    }

    public uint getALLOWED_INCREASE()
    {
        return ALLOWED_INCREASE;
    }

    public bool getIsSlowStart()
    {
        return IsSlowStart;
    }

    public uint getSsThres()
    {
        return SsThres;
    }
}
