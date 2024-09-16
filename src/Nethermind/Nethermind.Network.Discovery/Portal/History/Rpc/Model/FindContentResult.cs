// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Portal.History.Rpc.Model;

// TODO: Its a oneof
public class FindContentResult
{
    public string Content { get; set; } = null!;
    public bool UtpTransfer { get; set; }

    public string[] Enrs { get; set; } = Array.Empty<string>();
}
