// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;

namespace Ethereum.Test.Base;

public static class ChainUtils
{
    public static IReleaseSpec? ResolveSpec(IReleaseSpec? spec, ulong chainId)
    {
        if (chainId == BlockchainIds.Gnosis)
        {
            return spec switch
            {
                Prague => PragueGnosis.Instance,
                Cancun => CancunGnosis.Instance,
                Shanghai => ShanghaiGnosis.Instance,
                London => LondonGnosis.Instance,
                _ => spec
            };
        }
        return spec;
    }
}
