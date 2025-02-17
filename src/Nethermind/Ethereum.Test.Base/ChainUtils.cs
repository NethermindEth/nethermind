// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;

namespace Ethereum.Test.Base;

public class ChainUtils
{
    public static IReleaseSpec? AdjustSpecToGnosisChain(IReleaseSpec? spec, ulong chainId)
    {
        if (chainId != GnosisSpecProvider.Instance.ChainId)
        {
            return spec;
        }

        if (spec == Cancun.Instance)
        {
            return CancunGnosis.Instance;
        }
        if (spec == Prague.Instance)
        {
            return PragueGnosis.Instance;
        }

        return spec;
    }

    public static IReleaseSpec GetGenesisSpec(ulong chainId)
    {
        return chainId == GnosisSpecProvider.Instance.ChainId ? GnosisSpecProvider.Instance.GenesisSpec : MainnetSpecProvider.Instance.GenesisSpec;
    }

}
