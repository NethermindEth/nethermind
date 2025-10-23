// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Evm.State;

namespace Nethermind.Core.Test.Container;

public class FunctionalGenesisPostProcessor(IWorldState state, Action<Block, IWorldState> postProcessor) : IGenesisPostProcessor
{
    public void PostProcess(Block genesis)
    {
        postProcessor.Invoke(genesis, state);
    }
}
