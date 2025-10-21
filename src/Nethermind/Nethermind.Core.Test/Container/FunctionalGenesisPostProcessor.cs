// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;

namespace Nethermind.Core.Test.Container;

public class FunctionalGenesisPostProcessor(Action<Block> postProcessor) : IGenesisPostProcessor
{
    public void PostProcess(Block genesis)
    {
        postProcessor.Invoke(genesis);
    }
}
