// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.History.Proofs;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class NodeSeriesState
{
    private readonly byte[]?[] _refs = new byte[]?[BranchRlp.ChildCount];
    private NodeViewKind _kind = NodeViewKind.Empty;
    private byte[]? _wholeRlp;
    private ushort _presence;

    public void Apply(ReadOnlySpan<byte> row)
    {
        if (ParentRowCodec.IsEmptyRow(row))
        {
            Clear(NodeViewKind.Empty);
            return;
        }

        if (ParentRowCodec.IsWholeNodeRow(row))
        {
            Clear(NodeViewKind.Whole);
            _wholeRlp = ParentRowCodec.WholeNodeRlp(row).ToArray();
            return;
        }

        if (!ParentRowCodec.IsBranchRow(row)) throw new InvalidDataException("A commitment series row is neither a branch, a whole node nor an empty marker.");

        ushort presence = ParentRowCodec.Presence(row);
        ushort changed = ParentRowCodec.Changed(row);
        if (_kind != NodeViewKind.Branch) Clear(NodeViewKind.Branch);

        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((changed >> index) & 1) == 1 || ((presence >> index) & 1) == 0) _refs[index] = null;
        }

        ParentRowCodec.Fill(row, changed, _refs);
        _kind = NodeViewKind.Branch;
        _presence = presence;
    }

    public void MaterializeStart(CommitmentStore.RowChain newestAtOrBelow)
    {
        Apply(newestAtOrBelow.CurrentValue);
        if (_kind != NodeViewKind.Branch) return;

        ushort missing = Missing();
        while (missing != 0 && newestAtOrBelow.MoveNext())
        {
            ReadOnlySpan<byte> older = newestAtOrBelow.CurrentValue;
            if (!ParentRowCodec.IsBranchRow(older)) break;

            missing = (ushort)(missing & ~ParentRowCodec.Fill(older, missing, _refs));
        }

        if (missing != 0) throw new InvalidDataException("A commitment series starts with a branch row whose children cannot be filled from its own chain.");
    }

    public NodeView ToView()
    {
        switch (_kind)
        {
            case NodeViewKind.Empty:
                return NodeView.Empty;
            case NodeViewKind.Whole:
                return NodeView.Whole(_wholeRlp!);
            default:
                if (_presence == 0) return NodeView.Empty;
                if (Missing() != 0) throw new InvalidDataException("A commitment series row lists a child it never carried a reference for.");

                byte[]?[] copy = new byte[]?[BranchRlp.ChildCount];
                Array.Copy(_refs, copy, BranchRlp.ChildCount);
                return NodeView.Branch(copy);
        }
    }

    private ushort Missing()
    {
        ushort missing = 0;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((_presence >> index) & 1) == 1 && _refs[index] is null) missing |= (ushort)(1 << index);
        }

        return missing;
    }

    private void Clear(NodeViewKind kind)
    {
        _kind = kind;
        _wholeRlp = null;
        _presence = 0;
        Array.Clear(_refs);
    }
}
