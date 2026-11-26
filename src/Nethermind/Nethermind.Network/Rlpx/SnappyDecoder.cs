// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Snappier;

namespace Nethermind.Network.Rlpx;

/// <summary>Checks the size of received packets and decompresses the content of those sent with Snappy.</summary>
/// <remarks>
/// A packet over the size limit is reported rather than thrown for, because the session then disconnects the peer for a
/// breach of protocol instead of failing the frame.
/// </remarks>
internal static class SnappyDecoder
{
    /// <summary>Gets whether a received packet's content is within the size limit, compressed or not.</summary>
    public static bool IsWithinSizeLimit(int length) => length <= SnappyParameters.MaxSnappyLength;

    /// <summary>Reads the uncompressed length of compressed content, checks it against the size limit, and validates the block.</summary>
    /// <returns><see langword="false"/> when the uncompressed length is over the size limit.</returns>
    /// <exception cref="InvalidFrameException">The content is not a valid Snappy block.</exception>
    public static bool TryGetUncompressedLength(ReadOnlyMemory<byte> compressed, out int uncompressedLength, ILogger logger)
    {
        try
        {
            uncompressedLength = Snappy.GetUncompressedLength(compressed.Span);
        }
        catch (InvalidDataException exception)
        {
            LogDecompressionFailure(logger, compressed);
            throw new InvalidFrameException(exception);
        }

        if ((uint)uncompressedLength > (uint)SnappyParameters.MaxSnappyLength)
        {
            return false;
        }

        if (!SnappyBlockValidator.IsValid(compressed.Span, uncompressedLength))
        {
            LogDecompressionFailure(logger, compressed);
            throw new InvalidFrameException("Invalid Snappy block");
        }

        return true;
    }

    /// <summary>Decompresses content that <see cref="TryGetUncompressedLength"/> accepted straight into <paramref name="output"/>.</summary>
    /// <exception cref="InvalidFrameException">The content is not a valid Snappy block.</exception>
    public static void Decompress(ReadOnlyMemory<byte> compressed, IBufferWriter<byte> output, ILogger logger)
    {
        try
        {
            // The writer overload decompresses directly into the output, avoiding Snappier's temporary rental and copy.
            Snappy.Decompress(new ReadOnlySequence<byte>(compressed), output);
        }
        catch (InvalidDataException exception)
        {
            LogDecompressionFailure(logger, compressed);
            throw new InvalidFrameException(exception);
        }
    }

    private static void LogDecompressionFailure(ILogger logger, ReadOnlyMemory<byte> compressed)
    {
        if (logger.IsDebug)
        {
            ReadOnlyMemory<byte> prefix = compressed[..Math.Min(32, compressed.Length)];
            logger.Debug($"Snappy decompression failed for {compressed.Length} bytes: {prefix.ToHexString()}");
        }
    }
}
