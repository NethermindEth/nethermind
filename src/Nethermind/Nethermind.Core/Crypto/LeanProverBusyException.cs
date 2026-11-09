// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Crypto;

/// <summary>The prover refused a proof because its queue is full or it is shutting down; nothing was proven.</summary>
/// <remarks>A local, transient condition: callers retry on their next cycle or proceed without the proof.</remarks>
public sealed class LeanProverBusyException() : InvalidOperationException("The Lean prover is busy; retry later.");
