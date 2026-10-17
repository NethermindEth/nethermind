// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>The ordered calls and reverted sibling regions of one native source transaction.</summary>
internal sealed class NativeCallTree
{
    public List<Node> Calls { get; } = [];
    public List<int> Roots { get; } = [];

    public static NativeCallTree Read(StreamMessage[] messages, ref int index, ulong origin, ulong rollupId)
    {
        NativeCallTree tree = new();
        Stack<int> open = new();
        Stack<(int Depth, int First)> snapshots = new();
        while (index < messages.Length)
        {
            switch (messages[index++])
            {
                case StreamMessage.Call call:
                    tree.Push(open, origin, rollupId, call);
                    break;
                case StreamMessage.StaticCall call:
                    tree.Push(open, origin, rollupId, call);
                    break;
                case StreamMessage.Return result:
                    if (!open.TryPop(out int returned)) throw Invalid("return without an open call");
                    tree.Calls[returned].Result = result;
                    tree.Calls[returned].SubtreeEnd = tree.Calls.Count;
                    break;
                case StreamMessage.Snapshot:
                    snapshots.Push((open.Count, tree.Calls.Count));
                    break;
                case StreamMessage.Revert:
                    if (!snapshots.TryPop(out (int Depth, int First) snapshot) || snapshot.Depth != open.Count
                        || snapshot.First == tree.Calls.Count || tree.Calls[snapshot.First].RevertEnd != 0)
                    {
                        throw Invalid("invalid snapshot region");
                    }
                    tree.Calls[snapshot.First].RevertEnd = tree.Calls.Count;
                    break;
                case StreamMessage.Finish:
                    if (open.Count != 0 || snapshots.Count != 0 || tree.Calls.Count == 0) throw Invalid("empty or unfinished transaction");
                    tree.Validate();
                    return tree;
                default:
                    throw Invalid("unexpected transaction boundary");
            }
        }

        throw Invalid("unfinished transaction");
    }

    private void Push(Stack<int> open, ulong origin, ulong rollupId, StreamMessage message)
    {
        int parent = open.TryPeek(out int enclosing) ? enclosing : -1;
        ulong source = parent < 0 ? origin : Calls[parent].Target;
        (ulong target, ulong gas, CrossChainCall encoded) = message switch
        {
            StreamMessage.Call call => (call.ToChain, call.Gas,
                new CrossChainCall(0, false, call.Gas, call.From, source, call.To, call.Value, call.Data.ToArray())),
            StreamMessage.StaticCall call => (call.ToChain, call.Gas,
                new CrossChainCall(0, true, call.Gas, call.From, source, call.To, 0, call.Data.ToArray())),
            _ => throw Invalid("expected a call"),
        };
        if (gas != 0 || source == target || source != 0 && source != rollupId || target != 0 && target != rollupId)
        {
            throw Invalid("call gas or two-chain routing is invalid");
        }

        int index = Calls.Count;
        Calls.Add(new Node(index, parent, target, encoded));
        if (parent < 0) Roots.Add(index);
        else Calls[parent].Children.Add(index);
        open.Push(index);
    }

    private void Validate()
    {
        int regionEnd = 0;
        foreach (Node call in Calls)
        {
            if (!call.Result.Success && call.Children.Count != 0) throw Invalid("a failed call has descendants");
            if (call.Encoded.IsStatic)
            {
                if (call.RevertEnd != 0) throw Invalid("a static call starts a revert region");
                ValidateStatic(call);
            }

            if (call.RevertEnd == 0) continue;
            if (call.Index < regionEnd) throw Invalid("nested or overlapping revert regions");
            int cursor = call.Index;
            while (cursor < call.RevertEnd)
            {
                Node sibling = Calls[cursor];
                if (sibling.Parent != call.Parent) throw Invalid("revert region crosses sibling levels");
                cursor = sibling.SubtreeEnd;
            }
            if (cursor != call.RevertEnd) throw Invalid("revert region ends inside a call");
            regionEnd = cursor;
        }
    }

    private void ValidateStatic(Node call)
    {
        if (call.Children.Count == 0) return;
        if (call.Parent >= 0 || !call.Result.Success) throw Invalid("only a successful static root can have callbacks");
        foreach (int child in call.Children)
        {
            Node callback = Calls[child];
            if (!callback.Encoded.IsStatic || !callback.Result.Success || callback.Children.Count != 0
                || callback.Target != call.Encoded.SourceRollupId)
            {
                throw Invalid("a static root needs terminal successful static callbacks to its source");
            }
        }
    }

    internal static EezSettlementException Invalid(string reason) =>
        new(EezSettlementFailure.InvalidDaPayload, $"Invalid native call tree: {reason}.");

    internal sealed class Node(int index, int parent, ulong target, CrossChainCall encoded)
    {
        public int Index { get; } = index;
        public int Parent { get; } = parent;
        public ulong Target { get; } = target;
        public CrossChainCall Encoded { get; } = encoded;
        public List<int> Children { get; } = [];
        public StreamMessage.Return Result { get; set; } = null!;
        public int SubtreeEnd { get; set; }
        public int RevertEnd { get; set; }
        public ValueHash256 Hash { get; } = CrossChainCallHash.Compute(encoded.IsStatic, encoded.SourceAddress,
            encoded.SourceRollupId, encoded.TargetAddress, target, encoded.Value, 0, encoded.Data);
    }
}
