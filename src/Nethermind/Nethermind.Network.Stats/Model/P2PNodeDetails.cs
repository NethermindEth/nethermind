// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Stats.Model
{
    public class P2PNodeDetails
    {
        public byte P2PVersion { get; set; }
        public required string ClientId { get; set; }
        public required IReadOnlyList<Capability> Capabilities { get; set; }
        public int ListenPort { get; set; }
    }
}
