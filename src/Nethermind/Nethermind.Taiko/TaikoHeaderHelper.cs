// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Taiko;

/// <summary>
/// Helper methods for decoding Taiko block header extraData.
/// Shasta extraData layout: [basefeeSharingPctg (1 byte)][proposalId (6 bytes)]
/// </summary>
internal static class TaikoHeaderHelper
{
    public const int ShastaExtraDataBasefeeSharingPctgIndex = 0;
    public const int ShastaExtraDataProposalIDIndex = 1;
    public const int ShastaExtraDataProposalIDLength = 6;
    public const int ShastaExtraDataLen = 1 + ShastaExtraDataProposalIDLength;

    /// <summary>
    /// Decodes Ontake/Pacaya extraData to get basefeeSharingPctg (last byte, capped at 100).
    /// </summary>
    public static byte? DecodeOntakeExtraData(this BlockHeader header) =>
        header.ExtraData is { Length: >= 32 } ? Math.Min(header.ExtraData[31], (byte)100) : null;

    /// <summary>
    /// Decodes Shasta extraData to get basefeeSharingPctg (first byte).
    /// </summary>
    public static byte? DecodeShastaBasefeeSharingPctg(this BlockHeader header) =>
        header.ExtraData is { Length: < ShastaExtraDataLen } ? null : header.ExtraData[ShastaExtraDataBasefeeSharingPctgIndex];

    /// <summary>
    /// Decodes Shasta extraData to get proposalId (bytes 1-6).
    /// </summary>
    public static UInt256? DecodeShastaProposalID(this BlockHeader header)
    {
        if (header.ExtraData is null || header.ExtraData.Length < ShastaExtraDataLen)
        {
            return null;
        }

        ReadOnlySpan<byte> proposalIdBytes = header.ExtraData.AsSpan(ShastaExtraDataProposalIDIndex, ShastaExtraDataProposalIDLength);
        return new UInt256(proposalIdBytes, true);
    }
}
