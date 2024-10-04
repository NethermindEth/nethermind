// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Taiko;

internal static class TaikoHeaderHelper
{
    public static byte? GetBasefeeSharingPctg(this BlockHeader header) => header.ExtraData is { Length: >= 32 } ? header.ExtraData[31] : null;
}
