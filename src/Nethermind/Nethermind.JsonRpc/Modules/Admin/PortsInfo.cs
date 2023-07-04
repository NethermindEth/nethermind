// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class PortsInfo
    {
        [JsonProperty("discovery", Order = 0)]
        public int Discovery { get; set; }
        [JsonProperty("listener", Order = 1)]
        public int Listener { get; set; }
    }
}
