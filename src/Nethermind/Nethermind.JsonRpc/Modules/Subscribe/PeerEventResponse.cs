// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.JsonRpc.Modules.Subscribe;

public class PeerEventResponse
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }
    public string? Peer { get; set; }
    public string? Protocol { get; set; }
    public int? MsgPacketType { get; set; }
    public int? MsgSize { get; set; }
    public string? Local { get; set; }
    public string? Remote { get; set; }
    public string? Error { get; set; }
}
