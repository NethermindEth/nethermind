// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63;

/// <summary>
/// Checks an encoded receipts response against the request it answers, before any receipt is decoded.
/// </summary>
/// <remarks>
/// A block has one receipt per transaction, so a response holding more blocks than were requested, or more receipts
/// for a block than it has transactions, cannot pass validation. Counting RLP items allocates nothing, which rejects
/// such a response without decoding the receipts it carries.
/// </remarks>
internal static class ReceiptsResponseBudget
{
    /// <summary>
    /// Copies the receipt limits of the <paramref name="count"/> blocks starting at <paramref name="start"/>.
    /// </summary>
    /// <param name="expectedReceiptCounts">Per block, its transaction count, or a negative value when unknown.</param>
    /// <param name="start">The index of the first block in the request.</param>
    /// <param name="count">The number of blocks in the request.</param>
    /// <returns>The limits, or <see langword="null"/> when <paramref name="expectedReceiptCounts"/> is <see langword="null"/>.</returns>
    public static int[]? Slice(IReadOnlyList<int>? expectedReceiptCounts, int start, int count)
    {
        if (expectedReceiptCounts is null)
        {
            return null;
        }

        int[] limits = new int[count];
        for (int i = 0; i < limits.Length; i++)
        {
            int index = start + i;
            limits[i] = index < expectedReceiptCounts.Count ? expectedReceiptCounts[index] : -1;
        }

        return limits;
    }

    /// <summary>
    /// Throws when the receipts in <paramref name="content"/> exceed what the request allows.
    /// </summary>
    /// <param name="content">The encoded response.</param>
    /// <param name="fieldsBeforeReceipts">
    /// How many fields precede the receipts inside the response envelope (the request id, and for eth/70 the
    /// last-block-incomplete flag), or <see langword="null"/> when the response is the receipts list itself.
    /// </param>
    /// <param name="requestedBlocks">The number of blocks requested.</param>
    /// <param name="maxReceiptsPerBlock">
    /// Per requested block, the most receipts the response may hold for it, negative when unknown;
    /// <see langword="null"/> when no limit is known.
    /// </param>
    /// <exception cref="SubprotocolException">The response holds more blocks or receipts than allowed.</exception>
    /// <exception cref="RlpException">The response is not well-formed RLP.</exception>
    public static void ThrowIfExceeded(IByteBuffer content, int? fieldsBeforeReceipts, int requestedBlocks, int[]? maxReceiptsPerBlock)
    {
        RlpReader ctx = new(content.AsSpan());
        int limit = ctx.Length;
        if (fieldsBeforeReceipts is int fields)
        {
            limit = ReadSequenceEnd(ref ctx, limit);
            for (int i = 0; i < fields; i++)
            {
                SkipItem(ref ctx, limit);
            }
        }

        int end = ReadSequenceEnd(ref ctx, limit);
        int blocks = ctx.PeekNumberOfItemsRemaining(end, requestedBlocks + 1);
        if (blocks > requestedBlocks)
        {
            ThrowExceeded($"{blocks} blocks for {requestedBlocks} requested");
        }

        if (maxReceiptsPerBlock is null)
        {
            return;
        }

        int limitedBlocks = Math.Min(blocks, maxReceiptsPerBlock.Length);
        for (int i = 0; i < limitedBlocks; i++)
        {
            int maxReceipts = maxReceiptsPerBlock[i];
            if (maxReceipts < 0 || !ctx.IsSequenceNext())
            {
                SkipItem(ref ctx, end);
                continue;
            }

            int blockEnd = ReadSequenceEnd(ref ctx, end);
            if (ctx.PeekNumberOfItemsRemaining(blockEnd, maxReceipts + 1) > maxReceipts)
            {
                ThrowExceeded($"more than {maxReceipts} receipts for block {i}");
            }

            ctx.Position = blockEnd;
        }
    }

    // The RLP readers do not check declared lengths against the enclosing item, so every length is
    // checked here before anything inside it is read, which reports truncation as an RlpException.
    private static int ReadSequenceEnd(ref RlpReader ctx, int limit)
    {
        ThrowIfTruncated(ctx.Position < limit);
        int end = ctx.ReadSequenceLength() + ctx.Position;
        ThrowIfTruncated(end <= limit);
        return end;
    }

    private static void SkipItem(ref RlpReader ctx, int limit)
    {
        ThrowIfTruncated(ctx.Position < limit);
        ctx.SkipItem();
        ThrowIfTruncated(ctx.Position <= limit);
    }

    private static void ThrowIfTruncated(bool fits)
    {
        if (!fits)
        {
            throw new RlpException("Receipts response is truncated.");
        }
    }

    [StackTraceHidden, DoesNotReturn]
    private static void ThrowExceeded(string detail) => throw new SubprotocolException($"Receipts response exceeds the request: {detail}");
}
