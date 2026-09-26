// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>A settlement claim that the batch, the calldata or the observed execution does not support.</summary>
public sealed class EezSettlementException(string message) : Exception(message);
