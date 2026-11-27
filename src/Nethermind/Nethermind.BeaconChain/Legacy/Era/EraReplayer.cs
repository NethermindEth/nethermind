// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Legacy.Era;

/// <summary>Replays the chain from genesis out of <c>.era</c> files, and stores its newest finalized block as the anchor a node starts from.</summary>
/// <remarks>
/// Every era is replayed on its own from the state its predecessor's file claims, with all signatures and block state
/// roots verified, and the roots of its start and end states are recorded. No claimed state is trusted: the replay only
/// succeeds once each era starts where the previous one ended, back to the pinned genesis state. Recorded eras are
/// skipped by a later run. Execution payloads are not executed, which the execution layer's own sync makes up for.
/// </remarks>
public sealed partial class EraReplayer(BeaconChainSpec spec, BeaconChainStore store, IEraReplayConfig config, ILogManager logManager)
{
    /// <summary>Consecutive eras one lane replays, which spreads the cost of decoding its state and keys.</summary>
    private const int ErasPerLane = 8;

    /// <summary>Checkpoint blocks kept while looking for the finalized one, which is usually the third newest.</summary>
    private const int MaxCheckpoints = 8;

    private const int LinkLength = 2 * Hash256.Size;

    private readonly ILogger _logger = logManager.GetClassLogger<EraReplayer>();

    private static Hash256? GetGenesisStateRoot(ulong chainId) => chainId switch
    {
        BlockchainIds.Mainnet => new Hash256("0x7e76880eb67bbdc86250aa578958e9d0675e64e714337855204fb5abaaf82c2b"),
        _ => null,
    };

    /// <exception cref="InvalidConfigurationException">The network, the directory or the database does not allow a replay.</exception>
    /// <exception cref="InvalidDataException">An era file is malformed or invalid, or the eras do not form one chain.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired; finished eras stay recorded.</exception>
    public void Run(CancellationToken cancellationToken) =>
        Run(GetGenesisStateRoot(spec.ChainId) ?? throw Refuse($"no genesis state is pinned for chain id {spec.ChainId}"), cancellationToken);

    internal void Run(Hash256 genesisStateRoot, CancellationToken cancellationToken)
    {
        store.EnsureSchemaVersion();
        if (store.TryGetAnchor(out _, out ulong anchorSlot))
            throw Refuse($"the beaconChain database already has an anchor at slot {anchorSlot}");

        SortedDictionary<ulong, string> files = FindFiles();
        ulong endEra = config.EndEra ?? files.Keys.LastOrDefault();
        if (endEra == 0)
            throw Refuse("there is no era to replay");
        for (ulong era = 0; era <= endEra; era++)
        {
            if (!files.ContainsKey(era))
                throw Refuse($"the file of era {era} is missing from {config.Directory}");
        }

        ConcurrentDictionary<ulong, (Hash256 Start, Hash256 End)> links = [];
        List<(ulong First, ulong Last)> chunks = FindPendingChunks(endEra, links);
        int lanes = config.Lanes > 0 ? config.Lanes : Math.Max(1, Environment.ProcessorCount / 2);
        if (_logger.IsInfo) _logger.Info($"Replaying the beacon chain to era {endEra}: {chunks.Count} chunks of up to {ErasPerLane} eras are left, {lanes} at once");
        Parallel.ForEach(Partitioner.Create(chunks, EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = lanes, CancellationToken = cancellationToken },
            chunk => ReplayChunk(files, chunk.First, chunk.Last, links, cancellationToken));

        VerifyChain(links, genesisStateRoot, endEra);
        if (_logger.IsInfo) _logger.Info($"Verified the beacon chain from genesis to era {endEra}");

        if (spec.GetEpoch(EndSlot(endEra)) > spec.FuluForkEpoch)
            StoreAnchor(files, endEra, links[endEra].Start, cancellationToken);
    }

    private static ulong EndSlot(ulong era) => era * Presets.SlotsPerHistoricalRoot;

    private SortedDictionary<ulong, string> FindFiles()
    {
        if (!Directory.Exists(config.Directory))
            throw Refuse($"the directory '{config.Directory}' does not exist");
        SortedDictionary<ulong, string> files = [];
        foreach (string path in Directory.EnumerateFiles(config.Directory, "*.era"))
        {
            Match name = EraFileName().Match(Path.GetFileName(path));
            if (name.Success && !files.TryAdd(ulong.Parse(name.Groups["era"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture), path))
                throw Refuse($"there are several files of era {name.Groups["era"].Value} in {config.Directory}");
        }
        return files;
    }

    private List<(ulong First, ulong Last)> FindPendingChunks(ulong endEra, ConcurrentDictionary<ulong, (Hash256 Start, Hash256 End)> links)
    {
        List<(ulong First, ulong Last)> chunks = [];
        for (ulong era = 1; era <= endEra; era++)
        {
            if (TryGetLink(era, out Hash256 startRoot, out Hash256 endRoot))
            {
                links[era] = (startRoot, endRoot);
                continue;
            }
            if (chunks.Count > 0 && chunks[^1].Last == era - 1 && era - chunks[^1].First < ErasPerLane)
                chunks[^1] = (chunks[^1].First, era);
            else
                chunks.Add((era, era));
        }
        return chunks;
    }

    private void ReplayChunk(SortedDictionary<ulong, string> files, ulong first, ulong last, ConcurrentDictionary<ulong, (Hash256 Start, Hash256 End)> links, CancellationToken cancellationToken)
    {
        LegacyChain chain = LoadChain(files[first - 1], verifySignatures: true);
        Hash256 startRoot = chain.ComputeStateRoot();
        for (ulong era = first; era <= last; era++)
        {
            long started = Stopwatch.GetTimestamp();
            int blocks = 0;
            bool hasNext = files.TryGetValue(era + 1, out string? nextFile);
            try
            {
                using EraFileReader reader = new(files[era]);
                foreach (byte[] block in reader.ReadBlocks())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    chain.ApplyBlock(block);
                    blocks++;
                }
                chain.AdvanceTo(EndSlot(era));
                if (hasNext)
                    ApplyBoundaryBlock(chain, nextFile!);
            }
            catch (BeaconStateException e)
            {
                throw new InvalidDataException($"Era {era} is invalid after slot {chain.State.Slot}: {e.Message}", e);
            }

            Hash256 endRoot = chain.ComputeStateRoot();
            links[era] = (startRoot, endRoot);
            // Without the next file the end root may lack the boundary block, so a later run has to replay this era again.
            if (hasNext)
                PutLink(era, startRoot, endRoot);
            startRoot = endRoot;
            if (_logger.IsInfo) _logger.Info($"Replayed era {era}: {blocks} blocks, {chain.State.ReadOnlyValidators.Length} validators, {Stopwatch.GetElapsedTime(started).TotalSeconds:F0} s; {links.Count} eras are verified");
        }
    }

    /// <summary>Applies the block at the state's slot, if there is one, which the state at the end of the era contains.</summary>
    private static void ApplyBoundaryBlock(LegacyChain chain, string nextFile)
    {
        using EraFileReader reader = new(nextFile);
        foreach (byte[] block in reader.ReadBlocks())
        {
            if (SignedBeaconBlockCodec.TryReadSlot(block, out ulong slot) && slot == chain.State.Slot)
                chain.ApplyBlock(block);
            return;
        }
    }

    private static void VerifyChain(ConcurrentDictionary<ulong, (Hash256 Start, Hash256 End)> links, Hash256 genesisStateRoot, ulong endEra)
    {
        Hash256 expected = genesisStateRoot;
        for (ulong era = 1; era <= endEra; era++)
        {
            (Hash256 start, Hash256 end) = links[era];
            if (start != expected)
                throw new InvalidDataException($"Era {era} starts from state {start}, but the chain before it ends at state {expected}. Replace the file of era {era - 1} or {era} and delete the beaconChain database to replay again.");
            expected = end;
        }
    }

    /// <summary>Stores the block the last replayed era finalizes, with its post-state.</summary>
    /// <remarks>Replays that era once more, without signatures, and keeps the post-states of its last checkpoint blocks.</remarks>
    private void StoreAnchor(SortedDictionary<ulong, string> files, ulong endEra, Hash256 startRoot, CancellationToken cancellationToken)
    {
        LegacyChain chain = LoadChain(files[endEra - 1], verifySignatures: false);
        if (chain.ComputeStateRoot() != startRoot)
            throw new InvalidDataException($"The file of era {endEra - 1} changed since it was replayed.");

        Queue<(SignedBeaconBlock Block, BeaconStateFulu PostState)> checkpoints = [];
        SignedBeaconBlock? head = null;
        using EraFileReader reader = new(files[endEra]);
        foreach (byte[] ssz in reader.ReadBlocks())
        {
            cancellationToken.ThrowIfCancellationRequested();
            SignedBeaconBlock.Decode(ssz, out SignedBeaconBlock block);
            ulong lastEpochStart = (block.Message!.Slot - 1) / Presets.SlotsPerEpoch * Presets.SlotsPerEpoch;
            if (head is not null && head.Message!.Slot <= lastEpochStart)
            {
                checkpoints.Enqueue((head, chain.State.Clone()));
                if (checkpoints.Count > MaxCheckpoints)
                    checkpoints.Dequeue();
            }
            chain.ApplyBlock(ssz);
            head = block;
        }
        chain.AdvanceTo(EndSlot(endEra));

        Checkpoint finalized = chain.State.FinalizedCheckpoint!;
        foreach ((SignedBeaconBlock block, BeaconStateFulu postState) in checkpoints)
        {
            Hash256 root = SszRoots.HashTreeRoot(block.Message!);
            if (root != finalized.Root)
                continue;

            ulong slot = block.Message!.Slot;
            store.PutState(root, BeaconStateFulu.Encode(postState));
            store.StoreBlock(root, new ForkedSignedBeaconBlock.OfFulu(block), postState.LatestBlockHeader!.BodyRoot!);
            // Written last: its presence marks a complete anchor.
            store.SetAnchor(root, slot);
            if (_logger.IsInfo) _logger.Info($"Stored the finalized block {root} at slot {slot} (epoch {finalized.Epoch}) as the anchor");
            return;
        }
        throw new InvalidDataException($"The block {finalized.Root} that era {endEra} finalizes is not among its last {MaxCheckpoints} checkpoints; end the replay at an era that finalizes normally.");
    }

    private LegacyChain LoadChain(string stateFile, bool verifySignatures)
    {
        using EraFileReader reader = new(stateFile);
        return new LegacyChain(spec, reader.ReadState(), OptimisticNotifier.Instance, verifySignatures);
    }

    private static string LinkKey(ulong era) => $"eraLink:{era}";

    private void PutLink(ulong era, Hash256 startRoot, Hash256 endRoot)
    {
        byte[] link = new byte[LinkLength];
        startRoot.Bytes.CopyTo(link);
        endRoot.Bytes.CopyTo(link.AsSpan(Hash256.Size));
        store.PutMetadata(LinkKey(era), link);
    }

    private bool TryGetLink(ulong era, out Hash256 startRoot, out Hash256 endRoot)
    {
        if (store.GetMetadata(LinkKey(era)) is not { Length: LinkLength } link)
        {
            startRoot = endRoot = Hash256.Zero;
            return false;
        }
        startRoot = new Hash256(link.AsSpan(0, Hash256.Size));
        endRoot = new Hash256(link.AsSpan(Hash256.Size));
        return true;
    }

    private static InvalidConfigurationException Refuse(string reason) =>
        new($"Cannot replay era files: {reason}.", ExitCodes.ConflictingConfigurations);

    [GeneratedRegex(@"^[a-z0-9-]+-(?<era>[0-9]{5})-[0-9a-f]{8}\.era$")]
    private static partial Regex EraFileName();

    /// <summary>Leaves every payload to the execution layer's own sync.</summary>
    private sealed class OptimisticNotifier : INewPayloadNotifier
    {
        public static readonly OptimisticNotifier Instance = new();

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Optimistic;
    }
}
