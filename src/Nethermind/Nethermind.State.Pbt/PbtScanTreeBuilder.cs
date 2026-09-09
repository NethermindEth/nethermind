// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Folds sorted unique leaves without retaining completed subtrees.</summary>
internal sealed class PbtScanTreeBuilder(Action<IPbtNodePath, byte[]> visitNode)
{
    private readonly Frame[] _frontier = new Frame[PbtStorageFullKey.MaxLength * 8];
    private int _count;
    private Subtree _current;
    private bool _started;
    internal int PeakFrontier { get; private set; }

    internal void Add(PbtStorageFullKey key, ValueHash256 value)
    {
        if (_started)
        {
            if (_current.Key.CompareTo(key) >= 0) throw new InvalidDataException("PBT scan leaves must be strictly ordered.");
            int splitDepth = _current.Key.FirstDifferingBit(key);
            if (splitDepth >= Math.Min(_current.Key.BitLength, key.BitLength))
                throw new InvalidDataException("A PBT scan key cannot prefix another key.");
            while (_count != 0 && _frontier[_count - 1].SplitDepth >= splitDepth) Fold();
            _frontier[_count++] = new(_current, splitDepth);
            PeakFrontier = Math.Max(PeakFrontier, _count);
        }
        _current = new(key, -1, value, default);
        _started = true;
    }

    internal ValueHash256 Finish()
    {
        if (!_started) return default;
        while (_count != 0) Fold();
        return Complete(_current, 0);
    }

    private void Fold()
    {
        Frame frame = _frontier[--_count];
        ValueHash256 left = Complete(frame.Left, frame.SplitDepth + 1);
        ValueHash256 right = Complete(_current, frame.SplitDepth + 1);
        _current = new(frame.Left.Key, frame.SplitDepth, left, right);
    }

    private ValueHash256 Complete(Subtree subtree, int depth)
    {
        byte[] encoding;
        if (subtree.SplitDepth < 0)
        {
            encoding = PbtNodeCodec.EncodeLeaf(subtree.Key, subtree.Left.Bytes);
        }
        else
        {
            // A compressed branch's prefix starts after its parent's direction bit. Its placement
            // is only known when that parent closes, so the frontier defers encoding until then.
            int prefixBits = subtree.SplitDepth - depth;
            Span<byte> prefix = stackalloc byte[(prefixBits + 7) / 8];
            prefix.Clear();
            for (int bit = 0; bit < prefixBits; bit++)
                prefix[bit / 8] |= (byte)(subtree.Key.GetBit(depth + bit) << (7 - bit % 8));
            encoding = PbtNodeCodec.EncodeBranch(prefix, prefixBits, subtree.Left, subtree.Right);
        }
        Span<byte> path = stackalloc byte[(depth + 7) / 8];
        subtree.Key.Bytes[..path.Length].CopyTo(path);
        if (depth % 8 != 0) path[^1] &= (byte)(0xFF << (8 - depth % 8));
        visitNode(PbtPathOperations.Create(path, depth), encoding);
        return PbtNodeCodec.Hash(new PbtNodeReader(encoding));
    }

    private readonly record struct Frame(Subtree Left, int SplitDepth);
    private readonly record struct Subtree(PbtStorageFullKey Key, int SplitDepth, ValueHash256 Left, ValueHash256 Right);
}
