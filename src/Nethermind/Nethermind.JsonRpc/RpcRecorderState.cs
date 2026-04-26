// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;

namespace Nethermind.JsonRpc;

[Flags]
public enum RpcRecorderState
{
    [Description("None.")]
    None = 0,
    [Description("Records requests.")]
    Request = 1,
    [Description("Records responses.")]
    Response = 2,
    [Description("Records both requests and responses.")]
    All = Request | Response
}
