// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Portal.History.Rpc.Model;

public class NodeInfo
{
    public string Enr { get; set; } = null!;
    public string NodeId { get; set; } = null!;
}
