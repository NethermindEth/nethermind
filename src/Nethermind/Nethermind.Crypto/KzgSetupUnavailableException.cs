// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Crypto;

/// <summary>
/// The KZG trusted setup is unusable: it was never loaded, or its load failed.
/// </summary>
/// <remarks>
/// A node fault, not a verdict on the input being checked, so callers that turn exceptions into a failed proof
/// or a failed precompile call must let it propagate.
/// </remarks>
public sealed class KzgSetupUnavailableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
