// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;

namespace Nethermind.Facade.Find;

/// <summary>The receipts of a stored block are missing because the block is not processed yet, so a retry can succeed.</summary>
/// <remarks>Derives from <see cref="ResourceNotFoundException"/> so callers that handle missing receipts keep working.</remarks>
public sealed class BlockNotProcessedException(ulong blockNumber) : ResourceNotFoundException($"failed to get logs for block #{blockNumber}");
