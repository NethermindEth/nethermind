// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Portal.RpcModel;

// TODO: Its a oneof
public class FindContentResult
{
    public byte[]? Content { get; set; }
    public bool? UtpTransfer { get; set; }

    public string[]? Enrs { get; set; }
}
