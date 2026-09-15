// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Consensus.Validators
{
    public interface IHeaderValidator
    {
        bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error);

        /// <summary>Validates a header, optionally skipping the block-hash check.</summary>
        /// <remarks>
        /// The default implementation ignores <paramref name="validateHash"/> and validates fully, so an implementation
        /// that does not override it stays correct, just not faster.
        /// </remarks>
        /// <param name="validateHash">
        /// <c>false</c> only when the caller has already verified <see cref="BlockHeader.Hash"/> against the header
        /// contents; passing <c>false</c> otherwise accepts a header whose hash does not match what it describes.
        /// </param>
        bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error, bool validateHash) =>
            Validate(header, parent, isUncle, out error);
        bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error);
    }
}
