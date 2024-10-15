// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;

namespace Nethermind.JsonRpc;

[Flags]
public enum RpcRecorderState
{
    [Description]
    None = 0,
    [Description]
    Request = 1,
    [Description]
    Response = 2,
    [Description]
    All = Request | Response
}
