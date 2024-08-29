// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs
{
    public class SystemTransactionReleaseSpec(IReleaseSpec spec, bool isAura, bool isGenesis) : ReleaseSpecDecorator(spec)
    {
        private readonly IReleaseSpec _spec = spec;
        public override bool IsEip158Enabled => !isAura && isGenesis && _spec.IsEip158Enabled;
    }
}
