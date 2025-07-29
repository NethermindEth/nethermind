// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
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
        Error = e;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Type { get; set; }
    public string? Error { get; set; }
}
