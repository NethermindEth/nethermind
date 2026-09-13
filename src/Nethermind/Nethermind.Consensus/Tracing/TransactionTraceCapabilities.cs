// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Tracing;

/// <summary>Declares whether the processing environment supports transaction-only replay.</summary>
public sealed record TransactionTraceCapabilities(bool SupportsPrefixReplay = false);
