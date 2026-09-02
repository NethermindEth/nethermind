// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class SeriesPublisher(SeriesScope scope, TreePath path, SeriesKey? key, SeriesWriter writer)
{
    private NodeViewKind _lastKind = NodeViewKind.Empty;
    private byte[]?[]? _lastChildren;
    private ValueHash256 _lastHash;
    private bool _published;

    public bool IsNew(in ValueHash256 hash) => !_published || hash != _lastHash;

    public void Publish(ulong block, in NodeView view, CommitmentEmitter? emitter)
    {
        ushort changed = 0;
        if (view.Kind == NodeViewKind.Branch)
        {
            ushort presence = NodeViews.PresenceOf(view.Children!);
            changed = _lastKind == NodeViewKind.Branch ? NodeViews.ChangedChildren(_lastChildren, view.Children!) : presence;
            if (key is { Scratch: true } branchKey) writer.Write(branchKey, block, ParentRowCodec.EncodeBranch(block, presence, changed, view.Children!));
        }
        else if (key is { Scratch: true } otherKey)
        {
            writer.Write(otherKey, block, view.Kind == NodeViewKind.Whole ? ParentRowCodec.EncodeWholeNode(block, view.Rlp!) : ParentRowCodec.EncodeEmpty(block));
        }

        if (emitter is not null) scope.Record(emitter, path, view, changed);

        _lastKind = view.Kind;
        _lastChildren = view.Children;
        _lastHash = view.Hash;
        _published = true;
    }
}
