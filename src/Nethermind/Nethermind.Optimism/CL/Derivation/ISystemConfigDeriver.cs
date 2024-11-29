// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.CL;

public interface ISystemConfigDeriver
{
    SystemConfig SystemConfigFromL2Payload(ExecutionPayload l2Payload);
    SystemConfig UpdateSystemConfigFromL1BLock(SystemConfig systemConfig);
}

public struct SystemConfig
{
    public Address BatcherAddress;
    public ulong GasLimit;
}
