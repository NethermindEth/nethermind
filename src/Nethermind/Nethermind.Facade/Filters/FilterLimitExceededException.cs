// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Facade.Filters;

/// <summary>
/// Thrown by <see cref="FilterStore"/> when a filter is created or saved while the store already holds the max number of filters.
/// </summary>
public class FilterLimitExceededException(int maxFilters)
    : InvalidOperationException($"Too many filters installed. Max filters is {maxFilters}.");
