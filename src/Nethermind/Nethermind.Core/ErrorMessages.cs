// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public class ErrorMessages
{
    public static string BadBlock(Block? block, string reason)
    {
        return $"Invalid block {block?.ToString(Block.Format.FullHashNumberAndExtraData)} rejected, reason: {reason}";
    }
}
