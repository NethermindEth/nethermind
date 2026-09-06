// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.State.Flat.History.Proofs;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class NodeSeriesState : IDisposable
{
    private readonly ChildVector _refs = ChildVector.Rent();
    private NodeViewKind _kind = NodeViewKind.Empty;
    private byte[]? _whole;
    private int _wholeLength;
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
            ReadOnlySpan<byte> rlp = ParentRowCodec.WholeNodeRlp(row);
            if (_whole is null || _whole.Length < rlp.Length)
            {
                if (_whole is not null) ArrayPool<byte>.Shared.Return(_whole);
                _whole = ArrayPool<byte>.Shared.Rent(rlp.Length);
            }

            rlp.CopyTo(_whole);
            _wholeLength = rlp.Length;
            return;
        }

        if (!ParentRowCodec.IsBranchRow(row)) throw new InvalidDataException("A commitment series row is neither a branch, a whole node nor an empty marker.");

        ushort presence = ParentRowCodec.Presence(row);
        ushort changed = ParentRowCodec.Changed(row);
        if (_kind != NodeViewKind.Branch) Clear(NodeViewKind.Branch);

        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((changed >> index) & 1) == 1 || ((presence >> index) & 1) == 0) _refs.Clear(index);
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
                return NodeView.Whole(_whole.AsSpan(0, _wholeLength));
            default:
                if (_presence == 0) return NodeView.Empty;
                if (Missing() != 0) throw new InvalidDataException("A commitment series row lists a child it never carried a reference for.");

                ChildVector copy = ChildVector.Rent();
                copy.CopyFrom(_refs);
                return NodeView.Branch(copy);
        }
    }

    private ushort Missing()
    {
        ushort missing = 0;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((_presence >> index) & 1) == 1 && !_refs.IsPresent(index)) missing |= (ushort)(1 << index);
        }

        return missing;
    }

    private void Clear(NodeViewKind kind)
    {
        _kind = kind;
        _wholeLength = 0;
        _presence = 0;
        _refs.Clear();
    }

    public void Dispose()
    {
        ChildVector.Return(_refs);
        if (_whole is not null) ArrayPool<byte>.Shared.Return(_whole);
        _whole = null;
    }
}
