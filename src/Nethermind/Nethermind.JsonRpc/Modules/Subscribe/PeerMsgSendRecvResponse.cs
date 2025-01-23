// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Nethermind.JsonRpc.Modules.Admin;
using System;

namespace Nethermind.JsonRpc.Modules.Subscribe;

public class PeerMsgSendRecvResponse
{
    protected PeerMsgSendRecvResponse()
    {

    }

    public PeerMsgSendRecvResponse(EventArgs eventArgs, string subscripionType, string? e)
    {
        Type = subscripionType;
        //Peer = peerInfo.Id;
        //Local = peerInfo.Host;
        //Remote = peerInfo.Address;
        Error = e;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Type { get; set; }

    //public string Peer { get; set; }

    //public string Local { get; set; }

    //public string Remote { get; set; }

    public string? Error { get; set; }
}
