// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class SeriesPublisher(SeriesScope scope, TreePath path, SeriesKey? key, SeriesWriter writer) : IDisposable
{
    private readonly ChildVector _lastChildren = ChildVector.Rent();
    private NodeViewKind _lastKind = NodeViewKind.Empty;
    private ValueHash256 _lastHash;
    private bool _published;

    public bool IsNew(in ValueHash256 hash) => !_published || hash != _lastHash;

    public void Publish(ulong block, in NodeView view, CommitmentEmitter? emitter)
    {
        ushort changed = 0;
        if (view.Kind == NodeViewKind.Branch)
        {
            ChildVector children = view.Children!;
            ushort presence = children.Presence;
            changed = _lastKind == NodeViewKind.Branch ? children.ChangedSince(_lastChildren) : presence;
            if (key is { Scratch: true } branchKey) writer.WriteBranch(branchKey, block, presence, changed, children);
            _lastChildren.CopyFrom(children);
        }
        else if (key is { Scratch: true } otherKey)
        {
            if (view.Kind == NodeViewKind.Whole) writer.WriteWhole(otherKey, block, view.Rlp!);
            else writer.WriteEmpty(otherKey, block);
        }

        if (emitter is not null) scope.Record(emitter, path, view, changed);

        _lastKind = view.Kind;
        _lastHash = view.Hash;
        _published = true;
    }

    public void Dispose() => ChildVector.Return(_lastChildren);
}
