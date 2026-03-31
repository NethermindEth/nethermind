// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public static class Fork
{
    // Update this when a new fork is released on mainnet.
    public static NamedReleaseSpec GetLatest() => BPO2.Instance;
}
