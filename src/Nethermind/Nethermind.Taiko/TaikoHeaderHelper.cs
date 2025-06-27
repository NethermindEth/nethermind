// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Taiko;

internal static class TaikoHeaderHelper
{
    public static byte? DecodeOntakeExtraData(this BlockHeader header) => header.ExtraData is { Length: >= 32 } ? Math.Min(header.ExtraData[31], (byte)100) : null;
}
