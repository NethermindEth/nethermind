// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.State;
using System.Collections.Generic;

namespace Nethermind.Consensus.Requests;

public interface IRequestProcessor<T>
{
    IEnumerable<T> ReadRequests(Block block, IWorldState state, IReleaseSpec spec);
}
