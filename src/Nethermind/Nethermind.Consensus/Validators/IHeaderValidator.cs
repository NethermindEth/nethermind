// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Consensus.Validators
{
    public interface IHeaderValidator
    {
        bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error);

        /// <summary>Validates a header whose hash the caller has already verified against its contents.</summary>
        /// <remarks>The default implementation ignores <paramref name="validateHash"/> and validates fully.</remarks>
        bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error, bool validateHash) =>
            Validate(header, parent, isUncle, out error);
        bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error);
    }
}
