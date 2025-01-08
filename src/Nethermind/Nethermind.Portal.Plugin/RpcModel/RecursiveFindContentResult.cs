// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Portal.RpcModel;

public class RecursiveFindContentResult
{
    public byte[] Content { get; set; } = null!;
    public bool UtpTransfer { get; set; }
}
