// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class GetTrieNodesRequest
    {
        public Keccak RootHash { get; set; }

        public PathGroup[] AccountAndStoragePaths { get; set; }
    }
}
