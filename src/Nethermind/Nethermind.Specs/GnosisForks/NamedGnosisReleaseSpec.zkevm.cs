// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.Forks;

namespace Nethermind.Specs.GnosisForks;

public abstract partial class NamedGnosisReleaseSpec<TSelf>
{
    public static NamedReleaseSpec Instance => field ??= new TSelf();
}
