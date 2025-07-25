// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Specs
{
    public class SystemTransactionReleaseSpec(IReleaseSpec spec, bool isAura, bool isGenesis) : ReleaseSpecDecorator(spec)
    {
        public static IReleaseSpec GetReleaseSpec(IReleaseSpec spec, bool isAura, bool isGenesis)
        {
            // we only need the decorator if `spec.IsEip158Enabled` is true
            // AND either we're running Aura (so EIP-158 must be turned OFF)
            // OR it's not the genesis block (so EIP-158 must be turned OFF)
            if (spec.IsEip158Enabled && (isAura || !isGenesis))
            {
                return new SystemTransactionReleaseSpec(spec, isAura, isGenesis);
            }

            // otherwise wrapping won't change behavior, so just return the original
            return spec;
        }

        public override bool IsEip158Enabled => !isAura && isGenesis && base.IsEip158Enabled;
    }
}
