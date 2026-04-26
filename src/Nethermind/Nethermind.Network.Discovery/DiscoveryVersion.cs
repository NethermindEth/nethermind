// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery;

[Flags]
public enum DiscoveryVersion
{
    V4 = 0x1,
    V5 = 0x1 << 1,
    All = V4 | V5
}
