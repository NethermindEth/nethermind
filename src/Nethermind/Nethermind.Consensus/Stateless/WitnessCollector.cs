// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Stateless;

public interface IWitnessCollector
{
    Witness GetWitness(Hash256 parentHash, Hash256 parentStateRoot);
}


public class WitnessCollector(WitnessGeneratingBlockFinder blockFinder, WitnessGeneratingWorldState worldState) : IWitnessCollector
{
    public Witness GetWitness(Hash256 parentHash, Hash256 parentStateRoot)
    {
        (byte[][] stateNodes, byte[][] codes) = worldState.GetStateWitness(parentStateRoot);
        return new Witness()
        {
            _headers = blockFinder.GetWitnessHeaders(parentHash),
            Codes = codes,
            State = stateNodes,
        };
    }
}
