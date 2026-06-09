// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Utils;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// BTree cursor for <see cref="HsstEnumerator{TReader,TPin}"/>: indirect entries
/// reachable only by recursing the index tree. Streams the walk — keeps an ancestor
/// stack of (AbsStart, LastIdx) frames and the current leaf's metaStart values
/// buffered in a reusable array. Pinning a node isn't free for non-mmap readers,
/// so each leaf is loaded exactly once — every entry's metaStart is copied into
/// <c>_leafMetaStarts</c> up front, then MoveNext only pins the small LEB+key-length
/// window per entry. Memory is O(tree depth) for the ancestor stack plus one leaf's
/// worth of long offsets (typically a few hundred at most).
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
    // True for IndexType.BTreeKeyFirst, false for IndexType.BTree (entry layouts in FORMAT.md).
    private readonly bool _keyFirst;
    private readonly Ancestor[] _ancestors = new Ancestor[MaxDepth];

    // Current leaf state. _depth: -1 = not started, -2 = exhausted, ≥0 = leaf depth in tree.
    // _leafMetaStarts is sized to fit the current leaf and reused across leaves.
    private int _depth = -1;
    private long[] _leafMetaStarts = [];
    private int _leafCount;
    private int _leafIdx;

    // Current entry — populated by LoadCurrentEntry after positioning at a leaf.
    private long _currentKeyOffset;
    private long _currentKeyLength;
    private long _currentValueOffset;
    private long _currentValueLength;
    private long _currentMetaStart;

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
                int rootSize = tailBuf[1] | (tailBuf[2] << 8);
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

    // Streaming variant: total entry count is unknown without a full walk. Not used by
    // any caller today — keep the property for variant-shape parity but return -1.
    public long Count => -1;

    public bool MoveNext(scoped in TReader reader)
    {
        if (_depth == -2) return false;
        if (_depth == -1)
        {
            if (_rootAbsStart < 0)
            {
                _depth = -2;
                return false;
            }
            // First call: descend leftmost from root.
            if (!DescendToLeaf(in reader, _rootAbsStart, depthHint: 0))
            {
                _depth = -2;
                return false;
            }
            return LoadCurrentEntry(in reader);
        }

        _leafIdx++;
        if (_leafIdx < _leafCount)
        {
            return LoadCurrentEntry(in reader);
        }
        // Leaf exhausted — ascend until we find a sibling subtree.
        return AscendAndDescend(in reader);
    }

    public Bound CurrentKey => new(_currentKeyOffset, _currentKeyLength);
    public Bound CurrentValue => new(_currentValueOffset, _currentValueLength);
    public long CurrentMetadataStart => _currentMetaStart;

    /// <summary>
    /// Descend leftmost from the node starting at <paramref name="absStart"/> down to a leaf,
    /// pushing (AbsStart, LastIdx=0) ancestor frames as we cross intermediate levels. On
    /// success, _depth and the leaf metaStart buffer are populated with _leafIdx=0;
    /// returns false if a node fails to load or the tree exceeds MaxDepth. The root
    /// node gets its prefix bytes from <see cref="_rootPrefix"/>; deeper nodes are
    /// loaded with an empty parentSeparator since the enumerator only consumes value
    /// slots (the reader tolerates an absent prefix for value-only callers).
    /// </summary>
    private bool DescendToLeaf(scoped in TReader reader, long absStart, int depthHint)
    {
        long currentStart = absStart;
        int depth = depthHint;
        long scopeEndMinusTrailer = _scopeEnd - _trailerLen;
        Span<byte> flagBuf = stackalloc byte[1];
        while (depth < MaxDepth)
        {
            // Peek the flag byte to detect Entry-kind children (an entry record sitting
            // directly under an intermediate, via the direct-flush path in the builder).
            // Entries have no header, so we can't pass them to TryLoadNode — treat the
            // record as a single-entry virtual leaf at this depth.
            if (!reader.TryRead(currentStart, flagBuf)) return false;
            if ((BTreeNodeKind)(flagBuf[0] & 0x03) == BTreeNodeKind.Entry)
            {
                _depth = depth;
                if (_leafMetaStarts.Length < 1)
                    _leafMetaStarts = new long[16];
                _leafMetaStarts[0] = currentStart;
                _leafCount = 1;
                _leafIdx = 0;
                return true;
            }

            ReadOnlySpan<byte> parentSeparator = depth == 0 ? _rootPrefix : default;
            if (!HsstBTreeReader.TryLoadNode<TReader, TPin>(in reader, currentStart, scopeEndMinusTrailer, parentSeparator, out BTreeNodeReader node, out TPin pin))
                return false;

            using (pin)
            {
                // Empty index node (only happens for an empty HSST) — fall through to
                // ascent, which will exhaust and set _depth=-2.
                if (node.EntryCount == 0)
                {
                    _depth = depth;
                    _leafCount = 0;
                    _leafIdx = 0;
                    return AscendAndDescend(in reader);
                }

                // Peek the leftmost child's flag byte. The on-disk format no longer
                // distinguishes leaf from intermediate kinds; the descent decides
                // "buffer entries vs descend further" by inspecting children's kinds.
                long firstChildAbs = _scopeStart + (long)node.GetUInt64Value(0);
                if (!reader.TryRead(firstChildAbs, flagBuf)) return false;
                bool firstIsEntry = (BTreeNodeKind)(flagBuf[0] & 0x03) == BTreeNodeKind.Entry;
                if (firstIsEntry)
                {
                    // Verify ALL children are Entry-kind before treating the node as
                    // leaf-like. ChooseIntermediateChildCount packs descriptors
                    // consecutively without kind awareness, so a node may have mixed
                    // children (Entry from direct-flush + Intermediate from an inline
                    // page-local node). BufferLeaf relies on every value slot pointing
                    // at an entry record, so it must only fire when that holds.
                    bool allEntry = true;
                    int n = node.EntryCount;
                    for (int i = 1; i < n; i++)
                    {
                        long childAbs = _scopeStart + (long)node.GetUInt64Value(i);
                        if (!reader.TryRead(childAbs, flagBuf)) return false;
                        if ((BTreeNodeKind)(flagBuf[0] & 0x03) != BTreeNodeKind.Entry)
                        {
                            allEntry = false;
                            break;
                        }
                    }
                    if (allEntry)
                    {
                        _depth = depth;
                        BufferLeaf(node);
                        _leafIdx = 0;
                        return true;
                    }
                }

                // Mixed or inner node: push frame for this level, follow leftmost
                // child (which the next iteration will recognize as Entry or recurse
                // into as an Intermediate).
                ref Ancestor frame = ref _ancestors[depth];
                frame.AbsStart = currentStart;
                frame.LastIdx = 0;
                currentStart = firstChildAbs;
            }
            depth++;
        }
        return false;
    }

    /// <summary>
    /// Copy each entry's metaStart into the reusable buffer. Called once per leaf
    /// transition while the leaf pin is still live; subsequent in-leaf MoveNext
    /// calls index the array directly with no further node pinning.
    /// </summary>
    private void BufferLeaf(BTreeNodeReader leaf)
    {
        int n = leaf.EntryCount;
        if (_leafMetaStarts.Length < n)
        {
            int cap = Math.Max(16, _leafMetaStarts.Length);
            while (cap < n) cap *= 2;
            _leafMetaStarts = new long[cap];
        }
        for (int i = 0; i < n; i++)
        {
            _leafMetaStarts[i] = _scopeStart + (long)leaf.GetUInt64Value(i);
        }
        _leafCount = n;
    }

    /// <summary>
    /// Pop ancestors looking for a frame with another child to advance into; on success,
    /// descend leftmost from that child and load the first entry. Sets _depth=-2 when
    /// the whole tree is exhausted.
    /// </summary>
    private bool AscendAndDescend(scoped in TReader reader)
    {
        long scopeEndMinusTrailer = _scopeEnd - _trailerLen;
        while (_depth > 0)
        {
            _depth--;
            ref Ancestor anc = ref _ancestors[_depth];
            anc.LastIdx++;

            ReadOnlySpan<byte> parentSeparator = _depth == 0 ? _rootPrefix : default;
            if (!HsstBTreeReader.TryLoadNode<TReader, TPin>(in reader, anc.AbsStart, scopeEndMinusTrailer, parentSeparator, out BTreeNodeReader parent, out TPin parentPin))
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
            if (!DescendToLeaf(in reader, childAbsStart, depthHint: _depth + 1))
            {
                _depth = -2;
                return false;
            }
            return LoadCurrentEntry(in reader);
        }
        _depth = -2;
        return false;
    }

    /// <summary>
    /// Read entry _leafIdx's index pointer from the buffered leaf table, then pin a
    /// small window to decode the value length. Sets _currentKeyOffset/Length and
    /// _currentValueOffset/Length to absolute reader-space bounds.
    ///
    /// In both layouts the pointer aims at the entry's leading flag byte; the
    /// LEB128 (key-after-value) or FullKey (key-first) starts at <c>entryPos + 1</c>.
    /// Key-after-value mode (<c>_keyFirst = false</c>): MetadataStart = FlagByte,
    /// LEB128 at +1, value sits just before (<c>entryPos − valueLength</c>), key after.
    /// Key-first mode (<c>_keyFirst = true</c>): EntryStart = FlagByte, key at +1,
    /// LEB128 follows the key, value follows the LEB128.
    /// </summary>
    private bool LoadCurrentEntry(scoped in TReader reader)
    {
        long entryPos = _leafMetaStarts[_leafIdx];

        // Long LEB128 occupies up to 10 bytes; the key length comes from the trailer.
        const int ValueLenMaxBytes = 10;

        if (_keyFirst)
        {
            long keyStart = entryPos + 1;
            long lebStart = keyStart + _keyLength;
            int lebWindow = (int)Math.Min(ValueLenMaxBytes, _scopeEnd - lebStart);
            int pos;
            long valueLength;
            using (TPin lebPin = reader.PinBuffer(lebStart, lebWindow))
            {
                ReadOnlySpan<byte> leb = lebPin.Buffer;
                pos = 0;
                valueLength = Leb128.Read(leb, ref pos);
            }

            _currentMetaStart = entryPos;
            _currentKeyOffset = keyStart;
            _currentKeyLength = _keyLength;
            _currentValueOffset = lebStart + pos;
            _currentValueLength = valueLength;
            return true;
        }
        else
        {
            long lebStart = entryPos + 1;
            int lebWindow = (int)Math.Min(ValueLenMaxBytes, _scopeEnd - lebStart);
            int pos;
            long valueLength;
            using (TPin lebPin = reader.PinBuffer(lebStart, lebWindow))
            {
                ReadOnlySpan<byte> leb = lebPin.Buffer;
                pos = 0;
                valueLength = Leb128.Read(leb, ref pos);
            }

            _currentMetaStart = entryPos;
            _currentKeyOffset = lebStart + pos;
            _currentKeyLength = _keyLength;
            _currentValueOffset = entryPos - valueLength;
            _currentValueLength = valueLength;
            return true;
        }
    }
}
