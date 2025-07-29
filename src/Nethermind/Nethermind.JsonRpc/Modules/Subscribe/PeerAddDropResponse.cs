// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

using Nethermind.JsonRpc.Modules.Admin;

namespace Nethermind.JsonRpc.Modules.Subscribe;

public class PeerAddDropResponse
{
    public PeerAddDropResponse(PeerInfo peerInfo, string subscripionType, string? e)
    {
        Type = subscripionType;
        Peer = peerInfo.Id;
        Local = peerInfo.Host;
        Remote = peerInfo.Address;
        Error = e;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Type { get; set; }

    public string Peer { get; set; }

    public string Local { get; set; }

    public string Remote { get; set; }

    public string? Error { get; set; }
}
