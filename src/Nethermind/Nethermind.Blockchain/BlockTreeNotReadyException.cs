// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain;

/// <summary>The block tree temporarily cannot accept new blocks; the insertion may be retried.</summary>
public sealed class BlockTreeNotReadyException(string? message = null) : InvalidOperationException(message ?? "Cannot accept new blocks at the moment.");
