// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;

namespace Nethermind.JsonRpc;

public interface IRpcCapabilitiesProvider
{
    FrozenDictionary<string, RpcCapabilityOptions> GetEngineCapabilities();
}

[Flags]
public enum RpcCapabilityOptions : byte
{
    None = 0,
    Enabled = 1 << 0,
    WarnIfMissing = 1 << 1,
}

public static class RpcCapabilityOptionsExtensions
{
    public static bool IsEnabled(this RpcCapabilityOptions flags) => (flags & RpcCapabilityOptions.Enabled) != 0;

    public static bool ShouldWarnIfMissing(this RpcCapabilityOptions flags) => (flags & RpcCapabilityOptions.WarnIfMissing) != 0;
}
