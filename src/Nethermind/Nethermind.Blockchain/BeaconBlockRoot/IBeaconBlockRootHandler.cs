// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Consensus.BeaconBlockRoot;
public interface IBeaconBlockRootHandler
{
    void ApplyContractStateChanges(Block block, IReleaseSpec spec, IWorldState state);
}
