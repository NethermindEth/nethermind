// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;

namespace Ethereum.Test.Base;

public static class ChainUtils
{
    public static IReleaseSpec? ResolveSpec(IReleaseSpec? spec, ulong chainId)
    {
        return chainId == GnosisSpecProvider.Instance.ChainId
            ? spec == London.Instance ? LondonGnosis.Instance
            : spec == Cancun.Instance ? CancunGnosis.Instance
            : spec == Prague.Instance ? PragueGnosis.Instance
            : spec
            : spec;
    }
}
