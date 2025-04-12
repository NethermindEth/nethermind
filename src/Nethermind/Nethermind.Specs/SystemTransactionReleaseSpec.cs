// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Specs
{
    public class SystemTransactionReleaseSpec(IReleaseSpec spec, bool isAura, bool isGenesis) : ReleaseSpecDecorator(spec)
    {
        public override bool IsEip158Enabled => !isAura && isGenesis && base.IsEip158Enabled;
    }
}
