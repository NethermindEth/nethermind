// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Facade.Filters;

public sealed class FilterLimitExceededException(int limit)
    : InvalidOperationException($"Filter limit of {limit} per client exceeded");
