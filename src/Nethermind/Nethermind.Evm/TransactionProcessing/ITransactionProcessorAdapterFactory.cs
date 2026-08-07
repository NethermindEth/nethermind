// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>Builds the <see cref="ITransactionProcessorAdapter"/> wrapping a given tx processor.</summary>
/// <remarks>
/// Deliberately an interface, not a delegate: Autofac auto-synthesises factories for custom delegates,
/// which would wrongly populate an optional constructor parameter of this type on scopes that never
/// registered one. As an interface it stays null when unregistered, so callers fall back to the plain
/// <see cref="ExecuteTransactionProcessorAdapter"/>.
/// </remarks>
public interface ITransactionProcessorAdapterFactory
{
    /// <summary>Creates the adapter that wraps <paramref name="transactionProcessor"/>.</summary>
    ITransactionProcessorAdapter Create(ITransactionProcessor transactionProcessor);
}
