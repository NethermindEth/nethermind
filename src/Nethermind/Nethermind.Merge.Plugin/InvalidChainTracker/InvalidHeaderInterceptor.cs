// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Merge.Plugin.InvalidChainTracker;

public class InvalidHeaderInterceptor(
    IHeaderValidator headerValidator,
    IInvalidChainTracker invalidChainTracker,
    ILogManager logManager)
    : IHeaderValidator
{
    private readonly ILogger _logger = logManager.GetClassLogger<InvalidHeaderInterceptor>();

    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error)
    {
        bool result = headerValidator.Validate(header, parent, isUncle, out error);
        if (!result)
        {
            if (_logger.IsDebug) _logger.Debug($"Intercepted a bad header {header}");
            if (ShouldNotTrackInvalidation(header))
            {
                if (_logger.IsDebug) _logger.Debug($"Header invalidation should not be tracked");
                return result;
            }
            invalidChainTracker.OnInvalidBlock(header.Hash!, header.ParentHash);
        }
        invalidChainTracker.SetChildParent(header.Hash!, header.ParentHash!);
        return result;
    }

    public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error) =>
        headerValidator.ValidateOrphaned(header, out error);

    private static bool ShouldNotTrackInvalidation(BlockHeader header) => !HeaderValidator.ValidateHash(header);
}
