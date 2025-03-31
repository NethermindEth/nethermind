// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class InvalidBlockHelper
{
    public static string GetMessage(Block? block, string reason)
    {
        return $"Rejected invalid block {block?.ToString(Block.Format.FullHashNumberAndExtraData)}, reason: {reason}";
    }
}
