// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>Provides a validated, allocation-free view over a borrowed node-group payload.</summary>
public readonly ref struct PbtNodeGroupReader
{
    private readonly ReadOnlySpan<byte> _payload;
    private readonly int _entriesLength;
    private readonly uint _availability;
    private readonly ushort _descendantMask;

    /// <summary>Borrows a payload a store has already validated, reading only its footer's fixed fields.</summary>
    /// <remarks>Advancing the path afterwards cannot change this reader or its enumerators.
    /// The payload must remain valid and immutable for the lifetime of the reader and its enumerators.</remarks>
    internal static PbtNodeGroupReader FromValidated(scoped PbtTraversalPath path, ReadOnlySpan<byte> payload) => new(path, payload);

    // Nodes and descendant sizes are located on access by ranking their bit in the availability or descendant mask,
    // since a traversal reads only a few positions of each group it passes through.
    private PbtNodeGroupReader(scoped PbtTraversalPath path, ReadOnlySpan<byte> payload)
    {
        Debug.Assert(PbtFourLevelGroupGeometry.IsGroupDepth(path.BitDepth));
        payload = payload[PbtNodeGroupCodec.HeaderLength..];
        ushort descendantMask = PbtNodeGroupCodec.ReadDescendantMask(payload);
        uint availability = PbtNodeGroupCodec.ReadAvailability(payload);

        _payload = payload;
        _entriesLength = payload.Length - PbtNodeGroupCodec.GetTrailerLength(availability, payload);
        _availability = availability;
        _descendantMask = descendantMask;
    }

    /// <summary>Gets the summed payload lengths of the groups physically stored below boundary slot <paramref name="slot"/>.</summary>
    public long DescendantBytes(int slot) =>
        (_descendantMask & (1 << slot)) == 0 ? 0 : PbtNodeGroupCodec.ReadDescendantBytes(_payload, _descendantMask, slot);
    /// <summary>Gets the number of nodes in this group.</summary>
    public int Count => BitOperations.PopCount(_availability);
    /// <summary>Gets a borrowed canonical node encoding at a position.</summary>
    public ReadOnlySpan<byte> GetNode(int position) => (_availability & (1u << position)) == 0 ? [] : NodeAt(position);

    private ReadOnlySpan<byte> NodeAt(int position)
    {
        ReadOnlySpan<byte> offsets = _payload[(_entriesLength + BitOperations.PopCount(_availability & ((1u << position) - 1)) * sizeof(ushort))..];
        int start = BinaryPrimitives.ReadUInt16LittleEndian(offsets);
        int end = _availability >> (position + 1) == 0 ? _entriesLength : BinaryPrimitives.ReadUInt16LittleEndian(offsets[sizeof(ushort)..]);
        return _payload[start..end];
    }

    /// <summary>Gets an allocation-free positional enumerator.</summary>
    public Enumerator EnumerateNodes() => new(this);

    /// <summary>Enumerates present positions without allocating.</summary>
    public ref struct Enumerator
    {
        private readonly PbtNodeGroupReader _reader;
        private int _position;
        internal Enumerator(PbtNodeGroupReader reader) { _reader = reader; _position = -1; Current = default; CurrentPosition = -1; }
        /// <summary>Gets the current node encoding.</summary>
        public ReadOnlySpan<byte> Current { get; private set; }
        /// <summary>Gets the current post-order position.</summary>
        public int CurrentPosition { get; private set; }
        /// <summary>Advances to the next present node.</summary>
        public bool MoveNext()
        {
            while (++_position < PbtFourLevelGroupGeometry.PositionCount)
            {
                if ((_reader._availability & (1u << _position)) == 0) continue;
                CurrentPosition = _position; Current = _reader.NodeAt(_position); return true;
            }
            Current = default; CurrentPosition = -1; return false;
        }
    }
}
