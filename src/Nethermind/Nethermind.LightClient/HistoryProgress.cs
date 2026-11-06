// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.LightClient;

internal sealed record HistoryProgress(ulong? FirstVerifiedBlock, ulong? LastVerifiedBlock,
    ulong? TargetFirstBlock, ulong? TargetLastBlock, bool IsBackfilling, string? Error);
