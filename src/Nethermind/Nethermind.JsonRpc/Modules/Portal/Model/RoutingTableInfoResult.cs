// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Portal;

public class RoutingTableInfoResult
{
    public ValueHash256 LocalNodeId { get; set; }
    public ValueHash256[][] Buckets { get; set; }
}
