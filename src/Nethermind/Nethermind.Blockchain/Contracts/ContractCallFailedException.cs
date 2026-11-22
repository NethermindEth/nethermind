// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain.Contracts;

/// <summary>
/// Thrown when a contract call returns without a successful receipt status.
/// </summary>
/// <remarks>
/// Includes EVM failures (revert, out of gas, invalid opcode) and calls returning without a receipt.
/// Distinguished from an exception thrown while executing the call
/// (e.g. a state read failure), which <see cref="Contract"/> wraps in a plain <see cref="ContractException"/>.
/// </remarks>
public sealed class ContractCallFailedException(string message) : ContractException(message);
