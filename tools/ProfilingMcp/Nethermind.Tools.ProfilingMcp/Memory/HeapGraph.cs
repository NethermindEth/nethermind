// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.ProfilingMcp.Memory;

/// <summary>The live objects of a heap and their references, indexed by node number.</summary>
/// <remarks>References to addresses that were not reported as objects (such as frozen string literals) are dropped.</remarks>
internal sealed class HeapGraph
{
    private readonly ulong[] _sortedAddresses;
    private readonly int[] _sortedNodes;
    private readonly int[] _edgeStarts;
    private readonly int[] _edges;
    private readonly Dictionary<int, List<int>> _dependents = [];

    /// <param name="edgeStarts">For node <c>i</c>, its references are <paramref name="edgeTargets"/>[edgeStarts[i]..edgeStarts[i + 1]].</param>
    /// <param name="dependentHandles">Key and value addresses of dependent handles: the value is alive while the key is.</param>
    public HeapGraph(
        string[] typeNames,
        int[] nodeTypes,
        ulong[] addresses,
        long[] sizes,
        int[] edgeStarts,
        IReadOnlyList<ulong> edgeTargets,
        IEnumerable<(ulong Address, string Kind)> roots,
        IEnumerable<(ulong Key, ulong Value)> dependentHandles)
    {
        TypeNames = typeNames;
        NodeTypes = nodeTypes;
        Addresses = addresses;
        Sizes = sizes;
        _edgeStarts = edgeStarts;

        _sortedAddresses = (ulong[])addresses.Clone();
        _sortedNodes = new int[addresses.Length];
        for (int i = 0; i < _sortedNodes.Length; i++)
            _sortedNodes[i] = i;
        Array.Sort(_sortedAddresses, _sortedNodes);

        // Edges reported for objects the session missed (EventsLost) are dropped too.
        _edges = new int[Math.Min(edgeTargets.Count, edgeStarts[^1])];
        for (int i = 0; i < _edges.Length; i++)
            _edges[i] = IndexOf(edgeTargets[i]);

        List<(int Node, string Kind)> rootNodes = [];
        foreach ((ulong address, string kind) in roots)
        {
            if (IndexOf(address) is var node and >= 0)
                rootNodes.Add((node, kind));
        }
        Roots = rootNodes;

        foreach ((ulong key, ulong value) in dependentHandles)
        {
            if (IndexOf(key) is var keyNode and >= 0 && IndexOf(value) is var valueNode and >= 0)
            {
                if (!_dependents.TryGetValue(keyNode, out List<int>? values))
                    _dependents[keyNode] = values = [];
                values.Add(valueNode);
            }
        }
    }

    public string[] TypeNames { get; }

    /// <summary>Index into <see cref="TypeNames"/> per node.</summary>
    public int[] NodeTypes { get; }

    public ulong[] Addresses { get; }

    public long[] Sizes { get; }

    public IReadOnlyList<(int Node, string Kind)> Roots { get; }

    public int NodeCount => Addresses.Length;

    public string TypeName(int node) => TypeNames[NodeTypes[node]];

    /// <summary>Nodes the node references, followed by values of dependent handles keyed on it.</summary>
    public IEnumerable<int> References(int node)
    {
        (int start, int end) = EdgeRange(node);
        for (int i = start; i < end; i++)
        {
            if (_edges[i] >= 0)
                yield return _edges[i];
        }

        if (_dependents.TryGetValue(node, out List<int>? values))
        {
            foreach (int value in values)
                yield return value;
        }
    }

    /// <summary>Whether <paramref name="from"/> keeps <paramref name="to"/> alive only through a dependent handle, not a field.</summary>
    public bool IsDependentReference(int from, int to)
    {
        (int start, int end) = EdgeRange(from);
        return _dependents.TryGetValue(from, out List<int>? values) && values.Contains(to) && Array.IndexOf(_edges, to, start, end - start) < 0;
    }

    private (int Start, int End) EdgeRange(int node)
    {
        int end = Math.Min(_edgeStarts[node + 1], _edges.Length);
        return (Math.Min(_edgeStarts[node], end), end);
    }

    private int IndexOf(ulong address)
    {
        int index = Array.BinarySearch(_sortedAddresses, address);
        return index >= 0 ? _sortedNodes[index] : -1;
    }
}
