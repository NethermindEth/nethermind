// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BlockValidation.Data;

public static class BlockValidationStatus
{
    /// <summary>
    /// The submissions are invalid.
    /// </summary>
    public const string Invalid = "Invalid";

    /// <summary>
    /// The submissions are valid.
    /// </summary>
    public const string Valid = "Valid";
}