// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Taiko;

internal static class TaikoHeaderHelper
{
    public const int ShastaExtraDataMinLen = 2;

    public static byte? DecodeOntakeExtraData(this BlockHeader header) => header.ExtraData is { Length: >= 32 } ? Math.Min(header.ExtraData[31], (byte)100) : null;

    // Two bytes encoded in the extra data for Shasta
    // - First byte: basefeeSharingPctg
    // - Second byte: isLowBondProposal (lowest bit)
    // Returns only the basefeeSharingPctg
    public static byte? DecodeShastaExtraData(this BlockHeader header) => header.ExtraData is { Length: < ShastaExtraDataMinLen } ? null : header.ExtraData[0];
}
