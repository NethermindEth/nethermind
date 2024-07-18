// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Specs;

namespace Nethermind.Core.Test.Builders
{
    public partial class Build
    {
        public BlockTreeBuilder BlockTree(ISpecProvider? specProvider = null) => new(specProvider ?? MainnetSpecProvider.Instance);
        public BlockTreeBuilder BlockTree(Block genesisBlock, ISpecProvider? specProvider = null) => new(genesisBlock, specProvider ?? MainnetSpecProvider.Instance);
    }
}
