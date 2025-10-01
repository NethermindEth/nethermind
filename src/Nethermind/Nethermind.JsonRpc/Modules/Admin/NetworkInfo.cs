// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class NetworkInfo
    {
        public string LocalAddress { get; set; } = string.Empty;
        public string RemoteAddress { get; set; } = string.Empty;
        public bool Inbound { get; set; }
        public bool Trusted { get; set; }
        public bool Static { get; set; }

        [JsonIgnore]
        public string LocalHost { get; set; } = string.Empty;
    }
}
