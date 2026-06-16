// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// BTree cursor for <see cref="HsstEnumerator{TReader,TPin}"/>: indirect entries
/// reachable only by recursing the index tree. Streams the walk depth-first — keeps an
/// ancestor stack of (AbsStart, LastIdx) frames, descends to the leftmost entry, then on
/// each MoveNext ascends to the next sibling subtree and descends again. Each entry is
/// visited once; the parent node is reloaded once per sibling step. Memory is O(tree depth)
/// for the ancestor stack.
///
/// Heap-allocated so the dispatcher struct can be value-copied without losing iteration
/// state. Handles both <see cref="IndexType.BTree"/> (keyFirst=false) and
/// <see cref="IndexType.BTreeKeyFirst"/> (keyFirst=true); entry layouts in
/// <c>Hsst/FORMAT.md</c>.
/// </summary>
internal sealed class HsstBTreeEnumerator<TReader, TPin>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private const int MaxDepth = 16;

    private struct Ancestor { public long AbsStart; public int LastIdx; }

    private readonly long _scopeStart;
    private readonly long _scopeEnd;
    private readonly long _rootAbsStart;
    // Fixed key length read from the BTree trailer. Every entry in the HSST has a
    // key of exactly this many bytes — the data-section entry no longer repeats it.
    private readonly int _keyLength;
    private readonly bool _keyFirst;
    private readonly Ancestor[] _ancestors = new Ancestor[MaxDepth];

    // Walk state. _depth: -1 = not started, -2 = exhausted, ≥0 = the current entry's depth
    // in the tree. The entry's flag-byte position is threaded from the descent straight into
    // LoadCurrentEntry rather than stored.
    private int _depth = -1;

    private Bound _currentKey;
    private Bound _currentValue;

    // Root prefix bytes parsed from the HSST trailer at construction. Seeded as
    // parentSeparator when DescendToLeaf loads the root; non-root descents pass
    // `default` and rely on the value-only fast path in the reader (the enumerator
    // never touches prefix-dependent BTreeNode APIs — only GetUInt64Value /
    // EntryCount / BaseOffset).
    private readonly byte[] _rootPrefix;
    private readonly long _trailerLen;

    public HsstBTreeEnumerator(scoped in TReader reader, Bound scope, bool keyFirst)
    {
        _scopeStart = scope.Offset;
        _scopeEnd = scope.Offset + scope.Length;
        _keyFirst = keyFirst;
        _rootPrefix = [];
        // BTree trailer / root-location arithmetic: see Hsst/FORMAT.md, "BTree variant".
        // Smallest valid HSST: trailer (5 bytes) + root header (12 bytes).
        if (scope.Length >= 5 + 12)
        {
            Span<byte> tailBuf = stackalloc byte[5];
            if (reader.TryRead(_scopeEnd - 5, tailBuf))
            {
                int rootPrefixLen = tailBuf[0];
                int rootSize = BinaryPrimitives.ReadUInt16LittleEndian(tailBuf.Slice(1, 2));
                _keyLength = tailBuf[3];
                _trailerLen = 5L + rootPrefixLen;
                _rootAbsStart = _scopeEnd - _trailerLen - rootSize;
                if (rootPrefixLen > 0)
                {
                    _rootPrefix = new byte[rootPrefixLen];
                    if (!reader.TryRead(_scopeEnd - 5 - rootPrefixLen, _rootPrefix))
                    {
                        _rootAbsStart = -1;
                    }
                }
            }
            else
            {
                _rootAbsStart = -1;
            }
        }
        else
        {
            _rootAbsStart = -1;
        }
    }

    // Streaming variant: total entry count is unknown without a full walk.
    public long Count => -1;

    public bool MoveNext(scoped in TReader reader)
    {
        if (_depth == -2) return false;
        long entryPos;
        if (_depth == -1)
        {
            if (_rootAbsStart < 0)
            {
                _depth = -2;
                return false;
            }
            // First call: descend leftmost from root.
            if (!DescendToLeaf(in reader, _rootAbsStart, depthHint: 0, out entryPos))
            {
                _depth = -2;
                return false;
            }
        }
        else if (!AscendAndDescend(in reader, out entryPos))
        {
            return false;
        }
        return LoadCurrentEntry(in reader, entryPos);
    }

    public Bound CurrentKey => _currentKey;
    public Bound CurrentValue => _currentValue;

    /// <summary>
    /// Descend leftmost from the node starting at <paramref name="absStart"/> down to the
    /// leftmost entry, pushing (AbsStart, LastIdx=0) ancestor frames as we cross levels. On
    /// success _depth and <paramref name="entryPos"/> point at that entry; returns false if a node
    /// fails to load or the tree exceeds MaxDepth. The root node gets its prefix bytes from
    /// <see cref="_rootPrefix"/>; deeper nodes are loaded with an empty parentSeparator since
    /// the enumerator only consumes value slots (the reader tolerates an absent prefix for
    /// value-only callers).
    /// </summary>
    private bool DescendToLeaf(scoped in TReader reader, long absStart, int depthHint, out long entryPos)
    {
        entryPos = 0;
        long currentStart = absStart;
        int depth = depthHint;
        byte flag = 0;
        while (depth < MaxDepth)
        {
            // Peek the flag byte to detect Entry-kind children (an entry record sitting
            // directly under an intermediate, via the direct-flush path in the builder).
            // Entries have no header, so we can't pass them to TryLoadNode — treat the
            // record as a single-entry virtual leaf at this depth.
            if (!reader.TryRead(currentStart, new Span<byte>(ref flag))) return false;
            if ((BTreeNodeKind)(flag & 0x03) == BTreeNodeKind.Entry)
            {
                _depth = depth;
                entryPos = currentStart;
                return true;
            }

            ReadOnlySpan<byte> parentSeparator = depth == 0 ? _rootPrefix : default;
            if (!HsstBTreeReader.TryLoadNode<TReader, TPin>(in reader, currentStart, parentSeparator, out BTreeNodeReader node, out TPin pin))
                return false;

            using (pin)
            {
                // Empty index node (only happens for an empty HSST) — fall through to
                // ascent, which will exhaust and set _depth=-2.
                if (node.EntryCount == 0)
                {
                    _depth = depth;
                    return AscendAndDescend(in reader, out entryPos);
                }

                // Push a frame for this level and follow the leftmost child; the next
                // iteration recognizes it as an Entry (a single entry) or recurses into it
                // as an Intermediate. The on-disk format no longer distinguishes leaf from
                // intermediate kinds, so the descent decides purely by each child's flag.
                ref Ancestor frame = ref _ancestors[depth];
                frame.AbsStart = currentStart;
                frame.LastIdx = 0;
                currentStart = _scopeStart + (long)node.GetUInt64Value(0);
            }
            depth++;
        }
        return false;
    }

    /// <summary>
    /// Pop ancestors looking for a frame with another child to advance into; on success,
    /// descend leftmost from that child and load the first entry. Sets _depth=-2 when
    /// the whole tree is exhausted.
    /// </summary>
    private bool AscendAndDescend(scoped in TReader reader, out long entryPos)
    {
        entryPos = 0;
        while (_depth > 0)
        {
            _depth--;
            ref Ancestor anc = ref _ancestors[_depth];
            anc.LastIdx++;

            ReadOnlySpan<byte> parentSeparator = _depth == 0 ? _rootPrefix : default;
            if (!HsstBTreeReader.TryLoadNode<TReader, TPin>(in reader, anc.AbsStart, parentSeparator, out BTreeNodeReader parent, out TPin parentPin))
            {
                _depth = -2;
                return false;
            }
            long childAbsStart;
            using (parentPin)
            {
                // LastIdx is the semantic child index (0..N-1). With phantom slot 0
                // restored each child has its own slot, so EntryCount == N and the
                // exhaustion check is LastIdx >= EntryCount. Value[LastIdx] gives
                // the relative offset for children[LastIdx].
                if (anc.LastIdx >= parent.EntryCount) continue;
                long childRelStart = (long)parent.GetUInt64Value(anc.LastIdx);
                childAbsStart = _scopeStart + childRelStart;
            }
            if (!DescendToLeaf(in reader, childAbsStart, depthHint: _depth + 1, out entryPos))
            {
                _depth = -2;
                return false;
            }
            return true;
        }
        _depth = -2;
        return false;
    }

    /// <summary>
    /// Decode the entry at <paramref name="entryPos"/>: pin a small window to read the value
    /// length, then set <see cref="_currentKey"/> / <see cref="_currentValue"/> to absolute
    /// reader-space bounds.
    ///
    /// In both layouts the pointer aims at the entry's leading flag byte; the
    /// LEB128 (key-after-value) or FullKey (key-first) starts at <c>entryPos + 1</c>.
    /// Key-after-value mode (<c>_keyFirst = false</c>): MetadataStart = FlagByte,
    /// LEB128 at +1, value sits just before (<c>entryPos − valueLength</c>), key after.
    /// Key-first mode (<c>_keyFirst = true</c>): EntryStart = FlagByte, key at +1,
    /// LEB128 follows the key, value follows the LEB128.
    /// </summary>
    private bool LoadCurrentEntry(scoped in TReader reader, long entryPos)
    {
        // Long LEB128 occupies up to 10 bytes; the key length comes from the trailer.
        const int ValueLenMaxBytes = 10;

        if (_keyFirst)
        {
            long keyStart = entryPos + 1;
            long lebStart = keyStart + _keyLength;
            int lebWindow = (int)Math.Min(ValueLenMaxBytes, _scopeEnd - lebStart);
            int pos;
            long valueLength;
            using (TPin lebPin = reader.PinBuffer(new Bound(lebStart, lebWindow)))
            {
                ReadOnlySpan<byte> leb = lebPin.Buffer;
                pos = 0;
                valueLength = Leb128.Read(leb, ref pos);
            }

            _currentKey = new Bound(keyStart, _keyLength);
            _currentValue = new Bound(lebStart + pos, valueLength);
            return true;
        }
        else
        {
            long lebStart = entryPos + 1;
            int lebWindow = (int)Math.Min(ValueLenMaxBytes, _scopeEnd - lebStart);
            int pos;
            long valueLength;
            using (TPin lebPin = reader.PinBuffer(new Bound(lebStart, lebWindow)))
            {
                ReadOnlySpan<byte> leb = lebPin.Buffer;
                pos = 0;
                valueLength = Leb128.Read(leb, ref pos);
            }

            _currentKey = new Bound(lebStart + pos, _keyLength);
            _currentValue = new Bound(entryPos - valueLength, valueLength);
            return true;
        }
    }
}
