// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Walk;

internal readonly struct SeriesScope(bool isStorage, in ValueHash256 identity)
{
    public static readonly SeriesScope Accounts = new(isStorage: false, default);

    private readonly ValueHash256 _identity = identity;

    public bool IsStorage => isStorage;

    public ValueHash256 Identity => _identity;

    public static SeriesScope Storage(in ValueHash256 identity) => new(isStorage: true, identity);

    public SeriesKey Key(in TreePath path, bool scratch) => new(isStorage, _identity, path, scratch);

    public void Record(CommitmentEmitter emitter, in TreePath path, in NodeView view, ushort changedChildren)
    {
        if (isStorage)
        {
            if (view.Kind == NodeViewKind.Empty) emitter.RecordStorageEmpty(_identity, path);
            else emitter.RecordStorageNode(_identity, path, view.Rlp, changedChildren);
            return;
        }

        if (view.Kind == NodeViewKind.Empty) emitter.RecordAccountEmpty(path);
        else emitter.RecordAccountNode(path, view.Rlp, changedChildren);
    }
}
