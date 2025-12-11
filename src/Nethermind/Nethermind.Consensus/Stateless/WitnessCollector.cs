// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using System.Collections.Concurrent;
using Nethermind.Core.Specs;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

public interface IWitnessCollector
{
    Witness GetWitness(BlockHeader parentHeader, Block block);
}

public class WitnessCollector(WitnessGeneratingBlockFinder blockFinder, WitnessGeneratingWorldState worldState, IBlockProcessor blockProcessor, ISpecProvider specProvider) : IWitnessCollector
{
    public Witness GetWitness(BlockHeader parentHeader, Block block)
    {
        // ((OverlayTrieStore)worldState.TrieStore).DebugNodeCollector = new();
        ((OverlayTrieStore)worldState.TrieStore).RlpCollector = new();
        using (worldState.BeginScope(parentHeader))
        {
            (Block processed, TxReceipt[] receipts) = blockProcessor.ProcessOne(block, ProcessingOptions.DoNotUpdateHead & ProcessingOptions.ReadOnlyChain,
                NullBlockTracer.Instance, specProvider.GetSpec(block.Header));
            ConcurrentDictionary<Hash256, byte[]> touchedNodes = ((OverlayTrieStore)worldState.TrieStore).RlpCollector;
            byte[][] touchedNodesByteArrays = touchedNodes.Select(entry => entry.Value).ToArray();
            // ((OverlayTrieStore)worldState.TrieStore).DebugNodeCollector = null;
            ((OverlayTrieStore)worldState.TrieStore).RlpCollector = null;
            (byte[][] stateNodes, byte[][] codes, byte[][] keys) = worldState.GetStateWitness(parentHeader.StateRoot, touchedNodesByteArrays);
            return new Witness()
            {
                Headers = blockFinder.GetWitnessHeaders(parentHeader.Hash),
                Codes = codes,
                State = stateNodes,
                Keys = keys
            };
        }
    }
}
