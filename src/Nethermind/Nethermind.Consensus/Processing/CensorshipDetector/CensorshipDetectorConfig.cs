// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing.CensorshipDetector;

public class CensorshipDetectorConfig : ICensorshipDetectorConfig
{
    public bool Enabled { get; set; } = false;
    public uint BlockCensorshipThreshold { get; set; } = 2;
    public string[]? AddressesForCensorshipDetection { get; set; } = null;
}
