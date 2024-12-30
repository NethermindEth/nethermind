// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Portal.RpcModel;

public class NodeInfo
{
    public string Enr { get; set; } = null!;
    public string NodeId { get; set; } = null!;
}
