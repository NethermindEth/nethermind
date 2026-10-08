// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using static Nethermind.Benchmarks.State.FlatWorldStateBenchmarkHarness;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// The fixed cost of one read-only call scope: gather a bundle, open a <see cref="FlatWorldStateScope"/> on the no-op
/// trie warmer, emit the warm-up hints its writes would emit, then dispose it and return its pooled resources.
/// With <see cref="GrownResource"/> the pooled <see cref="TransientResource"/> the scope rents has the larger node
/// cache shards a prewarmed block leaves behind, so the measured return includes resetting them.
/// </summary>
[MemoryDiagnoser]
public class ReadOnlyScopeLifecycleBenchmark
{
    // Reset sizes the node cache for count / UtilRatio (0.25), so writing 64x its capacity grows it 256x.
    private const int GrowthFillMultiple = 64;

    private FlatDbConfig _config = null!;
    private ResourcePool _resourcePool = null!;
    private NoopTrieWarmer _warmer = null!;
    private Address[] _addresses = null!;

    [Params(0, 16)]
    public int HintedWrites { get; set; }

    [Params(false, true)]
    public bool GrownResource { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _config = new FlatDbConfig();
        _resourcePool = new ResourcePool(_config);
        _warmer = new NoopTrieWarmer();
        _addresses = new Address[16];
        for (int i = 0; i < _addresses.Length; i++) _addresses[i] = DeriveAddress(i + 1);
        if (GrownResource) GrowPooledResource();
    }

    [Benchmark]
    public void CreateHintDispose()
    {
        ReadOnlySnapshotBundle readOnly = new(
            new SnapshotPooledList(0), new NoopPersistenceReader(), recordDetailedMetrics: false, PersistedSnapshotStack.Empty());
        SnapshotBundle bundle = new(readOnly, new NullTrieNodeCache(), _resourcePool, ResourcePool.Usage.ReadOnlyProcessingEnv);
        using FlatWorldStateScope scope = new(
            new StateId(0, Keccak.EmptyTreeHash),
            bundle,
            new NullCodeDb(),
            new CapturingCommitTarget(),
            _config,
            _warmer,
            NullLogManager.Instance,
            isReadOnly: true);

        for (int i = 0; i < HintedWrites; i++)
        {
            scope.HintWarmAccount(_addresses[i]);
            scope.HintWarmSlot(_addresses[i], (UInt256)(ulong)i);
        }
    }

    private void GrowPooledResource()
    {
        TransientResource resource = _resourcePool.GetCachedResource(ResourcePool.Usage.ReadOnlyProcessingEnv);
        TrieNode node = new(NodeType.Leaf, Keccak.EmptyTreeHash);
        int fill = resource.Nodes.Capacity * GrowthFillMultiple;
        for (int i = 0; i < fill; i++) resource.Nodes.Set(null, TreePath.Empty, node);
        _resourcePool.ReturnCachedResource(ResourcePool.Usage.ReadOnlyProcessingEnv, resource);
    }
}
