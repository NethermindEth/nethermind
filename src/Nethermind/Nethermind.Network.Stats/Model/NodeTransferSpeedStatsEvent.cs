// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Stats.Model
{
    public class NodeTransferSpeedStatsEvent
    {
        public DateTime CaptureTime { get; set; }
        public long Latency { get; set; }
    }
}
