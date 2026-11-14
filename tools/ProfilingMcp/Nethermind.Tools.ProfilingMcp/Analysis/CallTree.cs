// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.ProfilingMcp.Analysis;

/// <summary>One method reached through one call path, with the time measured there.</summary>
public sealed class CallTreeNode(string name)
{
    private readonly Dictionary<string, CallTreeNode> _children = new(StringComparer.Ordinal);

    public string Name { get; } = name;
    public double TotalTime { get; internal set; }
    public double OwnTime { get; internal set; }
    public long? Samples { get; internal set; }

    /// <summary>Part of <see cref="TotalTime"/> spent in blocking waits at or below this node.</summary>
    public double WaitTime { get; internal set; }

    public IEnumerable<CallTreeNode> Children => _children.Values;

    internal CallTreeNode GetOrAddChild(string name)
    {
        if (!_children.TryGetValue(name, out CallTreeNode? child))
            _children[name] = child = new CallTreeNode(name);
        return child;
    }

    internal void RemoveChildren(Func<CallTreeNode, bool> predicate)
    {
        foreach (CallTreeNode child in _children.Values)
        {
            if (predicate(child))
                _children.Remove(child.Name);
        }
    }
}

/// <summary>Builds a call tree from the per-method call stacks of a trace.</summary>
internal static class CallTree
{
    /// <remarks>
    /// Each method is listed once per distinct call path, so every path becomes one node. A path prefix that is not listed
    /// gets the sum of its children's times.
    /// </remarks>
    public static CallTreeNode Build(IEnumerable<FunctionStat> functions, Func<string, bool> isWait)
    {
        CallTreeNode root = new("All threads");
        foreach (FunctionStat function in functions)
        {
            foreach (CallStackStat stack in function.CallStacks)
            {
                CallTreeNode node = root;
                foreach (string frame in stack.Frames)
                    node = node.GetOrAddChild(frame);

                node.TotalTime = stack.TotalTime;
                node.OwnTime = stack.OwnTime;
                node.Samples = stack.Samples;
            }
        }

        Complete(root, isWait);
        double totalTime = 0;
        double waitTime = 0;
        foreach (CallTreeNode child in root.Children)
        {
            totalTime += child.TotalTime;
            waitTime += child.WaitTime;
        }

        root.TotalTime = totalTime;
        root.WaitTime = waitTime;
        root.Samples = null;
        return root;
    }

    /// <summary>Drops nodes too small to see so that at most <paramref name="maxNodes"/> remain, and returns the number kept.</summary>
    /// <remarks>
    /// A node is kept when its time, or its time outside waits, is at least a fraction of the root's. The fraction starts at
    /// one in a million and doubles until the tree fits. Both times only grow towards the root, so every kept node keeps its ancestors.
    /// </remarks>
    public static int Prune(CallTreeNode root, int maxNodes)
    {
        int all = Count(root);
        if (all <= maxNodes)
            return all;

        double active = root.TotalTime - root.WaitTime;
        for (double fraction = 0.000001; ; fraction *= 2)
        {
            bool Keep(CallTreeNode n) => n.TotalTime >= fraction * root.TotalTime || (active > 0 && n.TotalTime - n.WaitTime >= fraction * active);

            int kept = Count(root, Keep);
            if (kept <= maxNodes || fraction >= 1)
            {
                RemoveAll(root, Keep);
                return kept;
            }
        }
    }

    public static int Count(CallTreeNode node) => Count(node, static _ => true);

    private static void Complete(CallTreeNode node, Func<string, bool> isWait)
    {
        double childTotal = 0;
        double childWait = 0;
        foreach (CallTreeNode child in node.Children)
        {
            Complete(child, isWait);
            childTotal += child.TotalTime;
            childWait += child.WaitTime;
        }

        if (node.TotalTime == 0)
            node.TotalTime = childTotal + node.OwnTime;
        node.WaitTime = isWait(node.Name) ? node.TotalTime : childWait;
    }

    private static int Count(CallTreeNode node, Func<CallTreeNode, bool> keep)
    {
        int count = 1;
        foreach (CallTreeNode child in node.Children)
        {
            if (keep(child))
                count += Count(child, keep);
        }

        return count;
    }

    private static void RemoveAll(CallTreeNode node, Func<CallTreeNode, bool> keep)
    {
        node.RemoveChildren(c => !keep(c));
        foreach (CallTreeNode child in node.Children)
            RemoveAll(child, keep);
    }
}
