// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;

public static class Extensions
{
    public static bool Validate(this IHeaderValidator headerValidator, BlockHeader header, BlockHeader? parent, bool isUncle = false)
    {
        return headerValidator.Validate(header, parent, isUncle, out _);
    }
    public static bool Validate(this IHeaderValidator headerValidator, BlockHeader header, bool isUncle = false)
    {
        return headerValidator.Validate(header, isUncle, out _);
    }
}
