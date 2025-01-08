// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Nethermind.JsonRpc.Modules.Admin;

namespace Nethermind.JsonRpc.Modules.Subscribe;

public class PeerAddDropResponse
{
    protected PeerAddDropResponse()
    {

    }

    public PeerAddDropResponse(PeerInfo peerInfo, string subscripionType, string? e)
    {
        Type = subscripionType;
        PeerId = peerInfo.Id;
        LocalAddr = peerInfo.Host;
        RemoteAddr = peerInfo.Address;
        Err = e;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("peer")]
    public string PeerId { get; set; }

    [JsonPropertyName("local")]
    public string LocalAddr { get; set; }

    [JsonPropertyName("remote")]
    public string RemoteAddr { get; set; }

    [JsonPropertyName("error")]
    public string? Err { get; set; }
}
