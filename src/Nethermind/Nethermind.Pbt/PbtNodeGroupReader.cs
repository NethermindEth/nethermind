// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>Provides a validated, allocation-free view over a borrowed node-group payload.</summary>
public readonly ref struct PbtNodeGroupReader
{
    private readonly int _groupDepth;
    private readonly ReadOnlySpan<byte> _payload;
    private readonly int _entriesLength;
    private readonly uint _availability;
    private readonly ushort _descendantMask;
    private readonly bool _initialized;

    /// <summary>Borrows a payload a store has already validated, reading only its footer's fixed fields.</summary>
    /// <remarks>Advancing the path afterwards cannot change this reader or its enumerators.
    /// The payload must remain valid and immutable for the lifetime of the reader and its enumerators.</remarks>
    internal static PbtNodeGroupReader FromValidated(scoped PbtTraversalPath path, ReadOnlySpan<byte> payload) => new(path, payload);

    // Nodes and descendant sizes are located on access by ranking their bit in the availability or descendant mask,
    // since a traversal reads only a few positions of each group it passes through.
    private PbtNodeGroupReader(scoped PbtTraversalPath path, ReadOnlySpan<byte> payload)
    {
        int groupDepth = path.BitDepth;
        Debug.Assert(PbtFourLevelGroupGeometry.IsGroupDepth(groupDepth));
        payload = payload[PbtNodeGroupCodec.HeaderLength..];
        ushort descendantMask = PbtNodeGroupCodec.ReadDescendantMask(payload);
        uint availability = PbtNodeGroupCodec.ReadAvailability(payload);

        _groupDepth = groupDepth;
        _payload = payload;
        _entriesLength = payload.Length - PbtNodeGroupCodec.GetTrailerLength(availability, payload);
        _availability = availability;
        _descendantMask = descendantMask;
        _initialized = true;
    }

    /// <summary>Gets the summed payload lengths of the groups physically stored below boundary slot <paramref name="slot"/>.</summary>
    public long DescendantBytes(int slot)
    {
        EnsureInitialized();
        if ((uint)slot >= PbtNodeGroupCodec.DescendantSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        return (_descendantMask & (1 << slot)) == 0 ? 0 : PbtNodeGroupCodec.ReadDescendantBytes(_payload, _descendantMask, slot);
    }
    /// <summary>Gets the number of nodes in this group.</summary>
    public int Count { get { EnsureInitialized(); return BitOperations.PopCount(_availability); } }
    /// <summary>Gets a borrowed canonical node encoding at a position.</summary>
    public ReadOnlySpan<byte> GetNode(int position)
    {
        ValidatePosition(position);
        return (_availability & (1u << position)) == 0 ? [] : NodeAt(position);
    }

    private ReadOnlySpan<byte> NodeAt(int position)
    {
        ReadOnlySpan<byte> offsets = _payload[(_entriesLength + BitOperations.PopCount(_availability & ((1u << position) - 1)) * sizeof(ushort))..];
        int start = BinaryPrimitives.ReadUInt16LittleEndian(offsets);
        int end = _availability >> (position + 1) == 0 ? _entriesLength : BinaryPrimitives.ReadUInt16LittleEndian(offsets[sizeof(ushort)..]);
        return _payload[start..end];
    }

    /// <summary>Gets an allocation-free positional enumerator.</summary>
    public Enumerator EnumerateNodes()
    {
        EnsureInitialized();
        return new(this);
    }

    private void ValidatePosition(int position)
    {
        EnsureInitialized();
        if ((uint)position >= PbtNodeGroupCodec.PositionCount || (position == PbtFourLevelGroupGeometry.RootPosition && _groupDepth != 0))
            throw new ArgumentOutOfRangeException(nameof(position));
    }
    private void EnsureInitialized() { if (!_initialized) throw new InvalidOperationException("The PBT node-group reader is not initialized."); }

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
            _reader.EnsureInitialized();
            while (++_position < PbtNodeGroupCodec.PositionCount)
            {
                if ((_reader._availability & (1u << _position)) == 0) continue;
                CurrentPosition = _position; Current = _reader.GetNode(_position); return true;
            }
            Current = default; CurrentPosition = -1; return false;
        }
    }
}
