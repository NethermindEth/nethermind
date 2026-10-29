// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public abstract partial class NamedReleaseSpec<TSelf>
{
    public static NamedReleaseSpec Instance { get; } = new TSelf();
}
