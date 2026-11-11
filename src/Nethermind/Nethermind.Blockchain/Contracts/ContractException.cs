// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain.Contracts;

/// <summary>
/// Thrown when a call to a contract cannot complete, or when its return data cannot be decoded.
/// </summary>
public class ContractException(string message, Exception? innerException = null) : Exception(message, innerException);
