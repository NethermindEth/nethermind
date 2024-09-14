// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules.Portal;

// TODO: Its a oneof
public class FindContentResult
{
    public string Content { get; set; }
    public bool UtpTransfer { get; set; }

    public string[] Enrs { get; set; }
}
