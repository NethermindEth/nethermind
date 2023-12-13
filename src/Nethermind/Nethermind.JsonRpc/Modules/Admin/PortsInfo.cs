// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class PortsInfo
    {
        [JsonPropertyName("discovery")]
        public int Discovery { get; set; }
        [JsonPropertyName("listener")]
        public int Listener { get; set; }
    }
}
