// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconNode.OApiClient.Configuration
{
    public class BeaconNodeConnection
    {
        public int ConnectionFailureLoopMillisecondsDelay { get; set; }
        public string[] RemoteUrls { get; set; } = new string[0];
    }
}
