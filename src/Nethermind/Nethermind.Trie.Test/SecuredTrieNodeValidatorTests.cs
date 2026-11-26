// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

/// <summary>Checks <see cref="SecuredTrieNodeValidator"/> against a reference written from the zkEVM spec's <c>incremental_mpt.py</c>.</summary>
/// <remarks>
/// The reference decodes the whole node with bounds-checked RLP, then applies the spec's rules; it shares no code with the
/// validator. Vectors worked out by hand from the spec pin both of them, and sweeps and seeded nodes compare the two. Each
/// node runs inside buffers of different bytes, so a decision that changes with these bytes fails the test. This does not
/// prove that no read leaves the node. <c>Nethermind.Trie.ZkEvm.Test</c> links this file, so it uses nothing but the BCL,
/// NUnit and the validator.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class SecuredTrieNodeValidatorTests
{
    private const int Invalid = -1;
    private const int Seed = 15788;
    private const int GeneratedNodes = 20_000;
    private const int KeyNibbles = 64;
    private const int HashLength = 32;
    // Lists nest in a node only inside a child embedded because it is shorter than a hash, so about 32 deep at most; the
    // bound keeps the reference's recursion off the stack limit for any input.
    private const int MaxNesting = 64;

    // Bytes around the node: an empty string, a one-byte string, an empty list, and prefixes of hashes and long forms.
    private static readonly byte[] Pads = [0x80, 0x00, 0xc0, 0xff, 0xa0, 0xb8];
    private static readonly byte[] BoundaryBytes = [0x00, 0x7f, 0x80, 0x81, 0x9f, 0xa0, 0xa1, 0xb7, 0xb8, 0xb9, 0xba, 0xc0, 0xc1, 0xde, 0xdf, 0xf7, 0xf8, 0xf9, 0xfa, 0xff];
    // With a path of 33 bytes, a value of 492 bytes makes a leaf as long as a branch of 16 hashes.
    private static readonly int[] ValueLengths = [0, 1, 2, 32, 54, 55, 56, 200, 255, 256, 492];
    private static readonly int[] JunkLengths = [1, 2, 31, 32, 33, 55, 56, 255, 256];

    private enum Shape { FullBranch, HashBranch, EmbeddedBranch, Leaf, Extension }

    // Notation: hex bytes; H is a hash reference, a0 then 32 bytes of 11; *n repeats an entry n times.
    // Leaves and paths (compact_to_nibbles, _decode_witness_node, _check_secured_path):
    [TestCase("c2 20 01", 64, false, 1)] // a leaf ends at the key
    [TestCase("c2 20 01", 63, false, Invalid)]
    [TestCase("c2 20 01", 64, true, Invalid)] // a node below the root short enough to embed, referenced by hash (_resolve)
    [TestCase("c2 30 80", 63, false, 1)] // flag 3: an odd leaf, whose first nibble shares the byte; an empty value
    [TestCase("c2 60 01", 64, false, 1)] // the flag's high bits are ignored
    [TestCase("c3 81 e0 01", 64, false, 1)]
    [TestCase("c2 2f 01", 64, false, 1)] // an even path's padding nibble is ignored
    [TestCase("c2 20 c0", 64, false, Invalid)] // a leaf's value is a string
    [TestCase("c3 20 81 01", 64, false, Invalid)] // a byte below 0x80 is its own encoding
    [TestCase("c3 81 20 01", 64, false, Invalid)]
    [TestCase("c2 80 01", 64, false, Invalid)] // an empty path
    [TestCase("c2 80 30", 65, false, Invalid)] // also where the next byte, read as its flags, would fit the depth
    [TestCase("c2 c0 80", 64, false, Invalid)] // a path that is a list
    [TestCase("c1 20", 64, false, Invalid)] // one item
    [TestCase("c3 20 01 01", 64, false, Invalid)] // three items
    [TestCase("c3 20 01", 64, false, Invalid)] // a list header one long
    [TestCase("c1 20 01", 64, false, Invalid)] // one short
    [TestCase("c2 20 01 00", 64, false, Invalid)] // a byte after the node
    [TestCase("80", 0, true, Invalid)] // the empty node, which the validator does not take
    [TestCase("c0", 0, true, Invalid)]
    [TestCase("", 0, true, Invalid)]
    [TestCase("e3 a1 20 00*32 01", 0, true, 1)] // a leaf of 64 nibbles
    [TestCase("e2 a0 30 00*31 01", 1, true, 1)] // 63 nibbles, a path that looks like a hash
    [TestCase("e2 a0 30 00*31 01", 2, true, Invalid)]
    [TestCase("de 20 9c 00*28", 64, true, Invalid)] // 31 bytes, short enough to embed, by hash (_resolve)
    [TestCase("df 20 9d 00*29", 64, true, 1)] // 32 bytes
    // Lengths (rlp.decode): canonical forms and the 55/56 and 255/256 boundaries of list and string.
    [TestCase("f7 20 b5 00*53", 64, true, 1)]
    [TestCase("f8 38 20 b6 00*54", 64, true, 2)]
    [TestCase("f8 39 20 b7 00*55", 64, true, 2)]
    [TestCase("f8 3b 20 b8 38 00*56", 64, true, 2)]
    [TestCase("f8 3a 20 b8 37 00*55", 64, true, Invalid)] // a long form not needed
    [TestCase("f8 37 20 b5 00*53", 64, true, Invalid)] // the list header in a long form not needed
    [TestCase("f8 ff 20 b8 fc 00*252", 64, true, 2)]
    [TestCase("f9 01 00 20 b8 fd 00*253", 64, true, 3)]
    [TestCase("f9 01 02 20 b8 ff 00*255", 64, true, 3)]
    [TestCase("f9 01 04 20 b9 01 00 00*256", 64, true, 3)]
    [TestCase("f8 44 20 b9 00 40 00*64", 64, true, Invalid)] // a length with a leading zero
    [TestCase("f8 02 20 01", 64, false, Invalid)] // the list header: a long form not needed
    [TestCase("f9 00 3b 20 b8 38 00*56", 64, false, Invalid)] // a leading zero
    [TestCase("f8", 64, false, Invalid)] // length bytes missing
    [TestCase("c2 20 b8", 64, false, Invalid)]
    [TestCase("f9 ff ff 20 b9 ff fb 00*65531", 64, true, 3)] // the longest payload the validator takes
    [TestCase("fa 01 00 00 20 b9 ff fc 00*65532", 64, true, Invalid)] // 64 KiB, where the spec has no limit
    // Extensions:
    [TestCase("e2 11 H", 62, true, 1)] // an extension stops short of the key
    [TestCase("e2 11 H", 63, true, Invalid)]
    [TestCase("c2 11 c0", 0, true, 1)] // the root may be short; an empty list is a child (_decode_child_ref)
    [TestCase("c2 11 c0", 1, true, Invalid)]
    [TestCase("c2 11 c0", 1, false, 1)]
    [TestCase("c2 00 c0", 0, false, Invalid)] // no nibbles
    [TestCase("c2 51 c0", 0, false, 1)] // flag 5, an odd extension
    [TestCase("c2 11 80", 0, false, Invalid)] // no child
    [TestCase("c2 11 01", 0, false, Invalid)] // a child reference of 1, 31 and 33 bytes
    [TestCase("e1 11 9f 00*31", 0, false, Invalid)]
    [TestCase("e3 11 a1 00*33", 0, false, Invalid)]
    [TestCase("e0 11 de 01*30", 0, false, 1)] // an embedded child of 31 bytes
    [TestCase("e1 11 df 01*31", 0, false, Invalid)] // of 32 bytes
    [TestCase("e0 11 de dd dc db da d9 d8 d7 d6 d5 d4 d3 d2 d1 d0 cf ce cd cc cb ca c9 c8 c7 c6 c5 c4 c3 c2 c1 c0", 0, false, 1)] // nested 31 deep
    [TestCase("c3 11 c1 81", 0, false, Invalid)] // an item that runs past its list
    [TestCase("f8 42 a0 00*32 H", 1, true, 2)] // 62 nibbles, a path that looks like a hash
    [TestCase("f8 42 a0 00*32 H", 2, true, Invalid)]
    // Branches:
    [TestCase("f9 02 11 H*16 80", 63, true, 0)]
    [TestCase("f9 02 11 H*16 80", 64, true, Invalid)] // a branch at the key's end
    [TestCase("f9 02 11 H*16 01", 0, true, Invalid)] // a branch with a value
    [TestCase("f8 51 H 80*14 H 80", 63, true, 0)]
    [TestCase("f8 51 H 80*14 H 80", 64, true, Invalid)]
    [TestCase("f1 H 80*16", 0, true, Invalid)] // one child
    [TestCase("d1 80*17", 0, true, Invalid)] // none
    [TestCase("d1 c0 80*16", 0, false, Invalid)]
    [TestCase("d1 c0 c0 80*15", 0, false, 0)] // empty lists are children
    [TestCase("d1 c0 c0 80*15", 64, false, Invalid)]
    [TestCase("d1 c0 c0 80*15", 1, true, Invalid)]
    [TestCase("f1 c0 H 80*15", 0, true, 0)]
    [TestCase("d5 c2 20 01 c2 20 01 80*15", 63, false, 0)] // embedded children
    [TestCase("d5 c2 20 01 c2 20 01 80*15", 64, false, Invalid)]
    [TestCase("d5 c2 20 01 c2 20 01 80*15", 63, true, Invalid)]
    [TestCase("d3 c2 20 01 80*16", 63, false, Invalid)]
    [TestCase("f8 4f de 01*30 H 80*15", 0, true, 0)]
    [TestCase("f8 50 df 01*31 H 80*15", 0, true, Invalid)]
    [TestCase("f8 52 c2 01 02 80*4 H 80*9 9f 00*31 80", 0, true, Invalid)] // an embedded child, then a string of 31 bytes
    [TestCase("f2 H c1 81 80*15", 0, true, Invalid)] // an embedded child whose item runs past it
    [TestCase("f3 c2 81 01 H 80*15", 0, true, Invalid)] // an embedded child that is not canonical
    [TestCase("f1 H 01 80*15", 0, true, Invalid)] // a child reference of 1, 31 and 33 bytes
    [TestCase("f8 50 H 9f 00*31 80*15", 0, true, Invalid)]
    [TestCase("f8 52 H a1 00*33 80*15", 0, true, Invalid)]
    [TestCase("f8 52 b8 20 00*32 H 80*15", 0, true, Invalid)] // a hash in a long form
    [TestCase("f8 50 H H 80*14", 0, true, Invalid)] // 16 items
    [TestCase("f8 52 H H 80*16", 0, true, Invalid)] // 18 items
    [TestCase("f8 51 H H 80*14 c0", 0, true, Invalid)] // a value that is a list
    public void Validate_matches_the_spec_on_known_nodes(string node, int depth, bool isHashed, int expected)
    {
        byte[] rlp = Hex(node);
        Assert.That(Expected(rlp, depth, isHashed), Is.EqualTo(expected), "reference");
        AssertValidateMatches(rlp, depth, isHashed, expected);
    }

    [Test]
    public void Validate_matches_the_reference_on_every_first_path_byte([Range(62, 65)] int depth)
    {
        byte[][] seconds = [[0x80], [0x00], [0xc0], [0xc1, 0x80], Hex("H"), Hex("9f 00*31"), Hex("de 01*30"), Hex("df 01*31")];
        for (int b = 0; b < 256; b++)
        {
            // A path of that byte; above 0x7f also the bare byte, which reads as an empty path, a longer string or a list.
            byte[][] paths = b < 0x80 ? [[(byte)b]] : [[0x81, (byte)b], [(byte)b]];
            foreach (byte[] path in paths)
            {
                foreach (byte[] second in seconds)
                {
                    byte[] rlp = List([path, second]);
                    AssertValidateMatches(rlp, depth, false, Expected(rlp, depth, false), pads: 2);
                }
            }
        }
    }

    [Test]
    public void Validate_matches_the_reference_on_every_value_of_a_full_branch_prefix([Values(63, 64)] int depth)
    {
        byte[] fullBranch = Hex("f9 02 11 H*16 80");
        int[] prefixes = [0, 1, 2, .. Enumerable.Range(0, 16).Select(static slot => 3 + 33 * slot), fullBranch.Length - 1];
        foreach (int at in prefixes)
        {
            for (int value = 0; value < 256; value++)
            {
                byte[] rlp = [.. fullBranch];
                rlp[at] = (byte)value;
                AssertValidateMatches(rlp, depth, true, Expected(rlp, depth, true), pads: 2);
            }
        }
    }

    [Test]
    public void Validate_matches_the_reference_on_generated_nodes()
    {
        Random random = new(Seed);
        int[,] outcomes = new int[5, 2];
        for (int i = 0; i < GeneratedNodes; i++)
        {
            Shape shape = (Shape)(i % 5);
            bool isFaultFree = Generate(random, shape, out byte[] rlp, out int depth, out bool isHashed);
            int expected = Expected(rlp, depth, isHashed);
            if (isFaultFree && expected == Invalid && !(isHashed && depth > 0 && rlp.Length < HashLength))
                Assert.Fail($"The reference rejects {Convert.ToHexStringLower(rlp)}, a {shape} built without a fault, at depth {depth}, isHashed {isHashed}");

            AssertValidateMatches(rlp, depth, isHashed, expected);
            outcomes[(int)shape, expected == Invalid ? 1 : 0]++;
        }

        // The generator must keep making both outcomes in each shape.
        using (Assert.EnterMultipleScope())
        {
            foreach (Shape shape in Enum.GetValues<Shape>())
            {
                Assert.That(outcomes[(int)shape, 0], Is.GreaterThan(GeneratedNodes / 5 / 20), $"{shape} accepted");
                Assert.That(outcomes[(int)shape, 1], Is.GreaterThan(GeneratedNodes / 5 / 20), $"{shape} rejected");
            }
        }
    }

    /// <summary>Checks <see cref="SecuredTrieNodeValidator.Validate"/> on <paramref name="rlp"/> inside buffers of different
    /// bytes, at different offsets, and <see cref="SecuredTrieNodeValidator.ValidateExtensionChild"/> on a node it takes.</summary>
    private static void AssertValidateMatches(byte[] rlp, int depth, bool isHashed, int expected, int pads = 6)
    {
        byte[] buffer = new byte[8 + Pads.Length + rlp.Length + 64];
        for (int p = 0; p < pads; p++)
        {
            Array.Fill(buffer, Pads[p]);
            rlp.CopyTo(buffer, 8 + p);
            nint actual;
            string? unexpected = null;
            try
            {
                actual = SecuredTrieNodeValidator.Validate(buffer.AsSpan(8 + p, rlp.Length), depth, isHashed);
                // A rejection must throw: a caller may ignore the return value.
                if (expected == Invalid) unexpected = $"a return of {actual} instead of a TrieException";
            }
            catch (TrieException e) when (e.GetType() == typeof(TrieException))
            {
                actual = Invalid;
            }
            catch (Exception e)
            {
                (actual, unexpected) = (int.MinValue, e.GetType().Name);
            }

            if (actual != expected || unexpected is not null)
                Assert.Fail($"Validate({Convert.ToHexStringLower(rlp)}, depth {depth}, isHashed {isHashed}) with pad {Pads[p]:x2}: expected {expected}, got {unexpected ?? actual.ToString()}");
        }

        if (expected == Invalid) return;

        // Its contract on a node that Validate takes: it passes a list of more than two items, a branch.
        Assert.That(() => SecuredTrieNodeValidator.ValidateExtensionChild(rlp), expected == 0 ? Throws.Nothing : Throws.TypeOf<TrieException>(),
            $"ValidateExtensionChild({Convert.ToHexStringLower(rlp)})");
    }

    /// <summary>An RLP item as the spec's <c>rlp.decode</c> gives it: a string's bytes or a list's items.</summary>
    private sealed record Item(byte[]? Bytes, Item[]? Items, int HeaderLength, int PayloadLength)
    {
        public int EncodedLength => HeaderLength + PayloadLength;
    }

    /// <summary>Decodes the canonical RLP item at the start of <paramref name="rlp"/>, or returns null if there is none.</summary>
    private static Item? Decode(ReadOnlySpan<byte> rlp, int nesting = 0)
    {
        if (rlp.IsEmpty || nesting > MaxNesting) return null;
        int prefix = rlp[0];
        if (prefix < 0x80) return new Item([rlp[0]], null, 0, 1);

        bool isList = prefix >= 0xc0;
        int header = 1;
        ulong length = (ulong)(prefix - (isList ? 0xc0 : 0x80));
        if (length > 55)
        {
            // A long form: the length follows in as many bytes as the prefix says, the first not zero, and is one the short
            // form cannot hold.
            header += (int)length - 55;
            if (rlp.Length < header || rlp[1] == 0) return null;
            length = 0;
            foreach (byte lengthByte in rlp[1..header]) length = length << 8 | lengthByte;
            if (length < 56) return null;
        }

        if (length > (ulong)(rlp.Length - header)) return null;
        ReadOnlySpan<byte> payload = rlp.Slice(header, (int)length);
        // A single byte below 0x80 is its own encoding.
        if (!isList) return payload is [< 0x80] ? null : new Item(payload.ToArray(), null, header, payload.Length);

        List<Item> items = [];
        for (int position = 0; position < payload.Length; position += items[^1].EncodedLength)
        {
            if (Decode(payload[position..], nesting + 1) is not { } item) return null;
            items.Add(item);
        }

        return new Item(null, [.. items], header, payload.Length);
    }

    /// <summary>What <see cref="SecuredTrieNodeValidator.Validate"/> must return for <paramref name="rlp"/>, or
    /// <see cref="Invalid"/> where it must throw.</summary>
    /// <remarks>
    /// The checks of the spec's <c>_resolve</c>: its length rule, <c>_decode_witness_node</c> and <c>_check_secured_path</c>. The
    /// reference follows the validator's contract where it is stricter than the spec: <c>_decode_witness_node</c> takes the
    /// empty string as the empty node, and <c>rlp.decode</c> limits no length, while a node here is a leaf, an extension or a
    /// branch, with a payload under 64 KiB.
    /// </remarks>
    private static int Expected(ReadOnlySpan<byte> rlp, int depth, bool isHashed)
    {
        // Only the root is referenced by hash whatever its length: a shorter node is embedded in its parent.
        if (isHashed && depth > 0 && rlp.Length < HashLength) return Invalid;
        if (Decode(rlp) is not { Items: { } items } node || node.EncodedLength != rlp.Length || node.PayloadLength > 0xffff) return Invalid;

        int remaining = KeyNibbles - depth;
        if (items.Length == 17)
        {
            int children = 0;
            for (int i = 0; i < 16; i++)
            {
                if (IsChild(items[i])) children++;
                else if (items[i].Bytes is not { Length: 0 }) return Invalid;
            }

            // The value is a string and counts as an entry, but a secured key never ends at a branch, so it must be empty.
            return items[16].Bytes is { Length: 0 } && children >= 2 && remaining > 0 ? 0 : Invalid;
        }

        // compact_to_nibbles: the path's first nibble flags a leaf with 2 and an odd number of nibbles with 1.
        if (items.Length != 2 || items[0].Bytes is not { Length: > 0 } path) return Invalid;
        int flag = path[0] >> 4;
        int nibbles = 2 * (path.Length - 1) + (flag & 1);
        bool isValid = (flag & 2) != 0
            ? items[1].Bytes is not null && nibbles == remaining
            : nibbles > 0 && nibbles < remaining && IsChild(items[1]);
        return isValid ? node.HeaderLength : Invalid;
    }

    /// <summary>The spec's <c>_decode_child_ref</c> on what is not the empty string: a hash, or a node embedded because its
    /// encoding is shorter than one.</summary>
    private static bool IsChild(Item item) => item.Bytes?.Length == HashLength || item is { Items: not null, EncodedLength: < HashLength };

    /// <summary>Builds a node of <paramref name="shape"/>, with one fault half the time, at a depth around the deepest it fits at.</summary>
    /// <returns>Whether no fault was applied. The spec takes such a node, unless it is shorter than a hash and referenced by hash
    /// below the root.</returns>
    private static bool Generate(Random random, Shape shape, out byte[] rlp, out int depth, out bool isHashed)
    {
        // A branch has a child in each half, so it keeps two unless a fault removes them.
        int lower = random.Next(8), upper = random.Next(8, 16), nibbles;
        List<byte[]> items;
        switch (shape)
        {
            case Shape.Leaf:
                nibbles = Pick(random, 0, 1, 2, 62, 63, 64, random.Next(65));
                items = [Str(Path(random, nibbles, true)), Str(Bytes(random, Pick(random, ValueLengths)))];
                depth = KeyNibbles - nibbles;
                break;
            case Shape.Extension:
                nibbles = Pick(random, 1, 2, 61, 62, 63, random.Next(1, 64));
                items = [Str(Path(random, nibbles, false)), random.Next(2) == 0 ? Hash(random) : EmbeddedNode(random)];
                depth = KeyNibbles - 1 - nibbles;
                break;
            default:
                items = [];
                int density = random.Next(4);
                for (int slot = 0; slot < 16; slot++)
                {
                    bool isOccupied = shape == Shape.FullBranch || slot == lower || slot == upper || random.Next(4) < density;
                    // The lower child of an embedded branch is embedded, so that a fault in a later slot comes after one.
                    items.Add(!isOccupied ? [0x80]
                        : shape == Shape.EmbeddedBranch && (slot == lower || random.Next(3) == 0) ? EmbeddedNode(random)
                        : Hash(random));
                }

                items.Add([0x80]);
                depth = KeyNibbles - 1;
                break;
        }

        // The node fits at this depth and at any nearer the root, but a leaf only here.
        int fit = depth;
        if (shape != Shape.Leaf && random.Next(2) == 0) depth = random.Next(fit + 1);
        isHashed = random.Next(4) != 0;
        rlp = List(items);
        if (random.Next(2) == 0) return true;

        // An item fault hits a leaf's value or an extension's child more often than its path.
        int index = shape is Shape.Leaf or Shape.Extension && random.Next(2) == 0 ? 1 : random.Next(items.Count);
        switch (random.Next(8))
        {
            case 0: // An item fewer or one more, under a header that stays right.
                if (random.Next(2) == 0) items.RemoveAt(index);
                else items.Insert(random.Next(items.Count + 1), Junk(random));
                break;
            case 1:
                items[index] = Junk(random);
                break;
            case 2: // An item in a long form it does not need.
                items[index] = Lengthen(items[index], random.Next(1, 3));
                break;
            case 3 when shape is not (Shape.Leaf or Shape.Extension): // One child or none.
                for (int slot = 0; slot < 16; slot++)
                {
                    if (slot != lower || random.Next(2) == 0) items[slot] = [0x80];
                }

                break;
            case 3 or 4: // Past the deepest fit, or a leaf short of it.
                depth = shape == Shape.Leaf && fit > 0 && random.Next(3) == 0 ? fit - 1 : fit + random.Next(1, 3);
                return false;
            case 5: // The list header in a long form it does not need, or with its length one off.
                if (random.Next(2) == 0)
                {
                    rlp = Lengthen(rlp, random.Next(1, 3));
                    return false;
                }

                int last = rlp[0] <= 0xf7 ? 0 : rlp[0] - 0xf7;
                rlp[last] = (byte)(rlp[last] + random.Next(2) * 2 - 1);
                return false;
            case 6: // A byte near the start or anywhere set to a prefix boundary, or moved by one.
                int at = random.Next(random.Next(2) == 0 ? Math.Min(4, rlp.Length) : rlp.Length);
                rlp[at] = random.Next(2) == 0 ? Pick(random, BoundaryBytes) : (byte)(rlp[at] + random.Next(2) * 2 - 1);
                return false;
            default: // Cut short, or followed by more bytes.
                rlp = random.Next(2) == 0 ? rlp[..Math.Max(1, rlp.Length - random.Next(1, 4))] : [.. rlp, .. Bytes(random, random.Next(1, 4))];
                return false;
        }

        rlp = List(items);
        return false;
    }

    /// <summary>A hex-prefix path of <paramref name="nibbles"/>, with random high flag bits and, if even, a random padding
    /// nibble: the spec ignores both.</summary>
    private static byte[] Path(Random random, int nibbles, bool isLeaf)
    {
        byte[] path = Bytes(random, nibbles / 2 + 1);
        path[0] = (byte)(((isLeaf ? 2 : 0) | (nibbles & 1) | random.Next(4) << 2) << 4 | path[0] & 0x0f);
        return path;
    }

    /// <summary>A list shorter than a hash: the empty list, a small leaf, nested lists, or the longest list that embeds.</summary>
    private static byte[] EmbeddedNode(Random random) => random.Next(4) switch
    {
        0 => [0xc0],
        1 => List([Str(Path(random, random.Next(3), true)), Str(Bytes(random, random.Next(4)))]),
        2 => List([List([Str(Bytes(random, random.Next(3)))])]),
        _ => Hex("de 01*30"),
    };

    /// <summary>An item to put where it may not belong: the wrong length or kind, not canonical, or a list too long to embed.</summary>
    private static byte[] Junk(Random random) => random.Next(9) switch
    {
        0 => [0x80],
        1 => [(byte)random.Next(0x80)],
        2 => Str(Bytes(random, Pick(random, JunkLengths))),
        3 => [0x81, (byte)random.Next(0x80)],
        4 => Lengthen(Str(Bytes(random, random.Next(60))), 1),
        5 => EmbeddedNode(random),
        6 => Hex(random.Next(2) == 0 ? "df 01*31" : "e0 01*32"),
        7 => [0xc1, 0x81],
        _ => Hash(random),
    };

    /// <summary>Re-encodes a canonical item with <paramref name="extra"/> length bytes more than it needs.</summary>
    private static byte[] Lengthen(byte[] item, int extra)
    {
        int prefix = item[0];
        int header = prefix < 0x80 ? 0 : (prefix & 0x3f) < 56 ? 1 : (prefix & 0x3f) - 54;
        byte[] payload = item[header..];
        return Wrap(prefix < 0xc0 ? 0x80 : 0xc0, payload, (payload.Length < 56 ? 0 : payload.Length < 256 ? 1 : 2) + extra);
    }

    /// <param name="lengthBytes">How many length bytes follow the prefix, 0 for the short form; by default as many as needed.</param>
    private static byte[] Wrap(int offset, byte[] payload, int lengthBytes = -1)
    {
        if (lengthBytes < 0) lengthBytes = payload.Length < 56 ? 0 : payload.Length < 256 ? 1 : 2;
        byte[] encoding = new byte[1 + lengthBytes + payload.Length];
        encoding[0] = (byte)(offset + (lengthBytes == 0 ? payload.Length : 55 + lengthBytes));
        for (int i = 0; i < lengthBytes; i++) encoding[lengthBytes - i] = (byte)(payload.Length >> 8 * i);
        payload.CopyTo(encoding, 1 + lengthBytes);
        return encoding;
    }

    private static byte[] Str(byte[] content) => content is [< 0x80] ? content : Wrap(0x80, content);

    private static byte[] List(IEnumerable<byte[]> items) => Wrap(0xc0, [.. items.SelectMany(static item => item)]);

    private static byte[] Hash(Random random) => Str(Bytes(random, HashLength));

    private static byte[] Bytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static T Pick<T>(Random random, params T[] values) => values[random.Next(values.Length)];

    /// <summary>Parses the notation of the vectors.</summary>
    private static byte[] Hex(string text)
    {
        List<byte> bytes = [];
        foreach (string entry in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split('*');
            byte[] unit = parts[0] == "H" ? [0xa0, .. Enumerable.Repeat((byte)0x11, HashLength)] : [Convert.ToByte(parts[0], 16)];
            for (int i = parts.Length == 2 ? int.Parse(parts[1]) : 1; i > 0; i--) bytes.AddRange(unit);
        }

        return [.. bytes];
    }
}
