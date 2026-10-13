// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;

namespace Nethermind.Blockchain.Contracts;

/// <summary>
/// Thrown when a contract call runs to completion but ends with a failed status (revert, out of gas, invalid opcode).
/// </summary>
/// <remarks>
/// Distinguishes a failure of the contract code itself from an exception thrown while executing the call
/// (e.g. a state read failure), which <see cref="Contract"/> wraps in a plain <see cref="AbiException"/>.
/// </remarks>
public sealed class ContractCallFailedException(string message) : AbiException(message);
