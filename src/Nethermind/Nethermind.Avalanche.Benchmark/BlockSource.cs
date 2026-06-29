// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Avalanche.Benchmark;

/// <summary>
/// Loads a contiguous range of blocks from a local RLP source: either a directory of per-block
/// <c>.rlp</c> files (one standard Ethereum block RLP per file) or a single file holding the block
/// RLPs concatenated back-to-back.
/// </summary>
/// <remarks>
/// The harness measures EVM transaction execution and therefore uses <b>standard Ethereum/Nethermind
/// block RLP</b> (header || transactions || uncles [ || withdrawals ]). Avalanche's
/// <c>extData</c> / atomic-transaction payload is intentionally ignored — it carries no EVM
/// transactions and does not affect the C-Chain EVM execution being measured. Blocks exported via
/// <c>debug_getRawBlock</c> over <c>/ext/bc/C/rpc</c> are already in this canonical form; see the
/// project README for export instructions.
/// </remarks>
public static class BlockSource
{
    private static readonly BlockDecoder Decoder = new();

    /// <summary>
    /// Loads and RLP-decodes all blocks from <paramref name="path"/>, sorted ascending by block number.
    /// </summary>
    /// <param name="path">A directory of <c>*.rlp</c> files or a single concatenated RLP file.</param>
    /// <returns>The decoded blocks in ascending block-number order.</returns>
    public static IReadOnlyList<Block> Load(string path)
    {
        if (Directory.Exists(path))
        {
            return LoadFromDirectory(path);
        }

        if (File.Exists(path))
        {
            return LoadFromConcatenatedFile(path);
        }

        throw new FileNotFoundException($"Block source '{path}' is neither an existing directory nor file.", path);
    }

    private static IReadOnlyList<Block> LoadFromDirectory(string directory)
    {
        List<Block> blocks = [];
        foreach (string file in Directory.EnumerateFiles(directory, "*.rlp", SearchOption.TopDirectoryOnly))
        {
            byte[] bytes = ReadRlpBytes(file);
            blocks.Add(Decode(bytes));
        }

        if (blocks.Count == 0)
        {
            throw new InvalidOperationException($"No '*.rlp' files found in directory '{directory}'.");
        }

        return SortByNumber(blocks);
    }

    private static IReadOnlyList<Block> LoadFromConcatenatedFile(string file)
    {
        byte[] bytes = ReadRlpBytes(file);
        List<Block> blocks = [];

        // Walk the concatenated stream item-by-item. Each top-level RLP list is one block; the public
        // RlpReader.PeekNextRlpLength() reports the full item length (prefix + content) at the cursor.
        int offset = 0;
        while (offset < bytes.Length)
        {
            int itemLength;
            {
                RlpReader peeker = new(bytes.AsSpan(offset));
                itemLength = peeker.PeekNextRlpLength();
            }

            ReadOnlySpan<byte> slice = bytes.AsSpan(offset, itemLength);
            blocks.Add(Decoder.Decode(slice, RlpBehaviors.AllowExtraBytes));
            offset += itemLength;
        }

        if (blocks.Count == 0)
        {
            throw new InvalidOperationException($"No blocks decoded from concatenated file '{file}'.");
        }

        return SortByNumber(blocks);
    }

    /// <summary>
    /// Reads RLP bytes from a file, accepting either raw binary or a single <c>0x</c>-prefixed hex string.
    /// </summary>
    private static byte[] ReadRlpBytes(string file)
    {
        byte[] raw = File.ReadAllBytes(file);
        // Heuristic: a hex export starts with the ASCII '0' 'x' prefix (0x30 0x78). Raw block RLP is a
        // list and always starts with a prefix byte >= 0xC0, so there is no ambiguity.
        if (raw.Length >= 2 && raw[0] == (byte)'0' && (raw[1] == (byte)'x' || raw[1] == (byte)'X'))
        {
            string hex = System.Text.Encoding.ASCII.GetString(raw).Trim();
            return Convert.FromHexString(hex.AsSpan(2));
        }

        return raw;
    }

    private static Block Decode(byte[] bytes) => Decoder.Decode(bytes.AsSpan(), RlpBehaviors.AllowExtraBytes);

    private static IReadOnlyList<Block> SortByNumber(List<Block> blocks)
    {
        blocks.Sort(static (a, b) => a.Number.CompareTo(b.Number));
        for (int i = 1; i < blocks.Count; i++)
        {
            if (blocks[i].Number != blocks[i - 1].Number + 1)
            {
                throw new InvalidOperationException(
                    $"Loaded blocks are not contiguous: block {blocks[i - 1].Number} is followed by {blocks[i].Number}.");
            }
        }

        return blocks;
    }
}
