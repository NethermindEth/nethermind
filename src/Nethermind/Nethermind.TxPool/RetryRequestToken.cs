// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TxPool;

internal readonly record struct RetryRequestToken<TResourceId>(
    TResourceId ResourceId,
    long Generation,
    long Attempt,
    long Deadline,
    RetryRequestConfirmation? Confirmation = null) where TResourceId : struct;
