// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Facade;

/// <summary>Wraps the transaction processor an <c>eth_call</c> runs on.</summary>
/// <remarks>
/// A chain registers one to give <c>eth_call</c> rules of its own, such as running calls without charging fees, by
/// composing them around its transaction processor. Without one, <c>eth_call</c> runs on the processor itself.
/// <para>
/// The wrapper runs everything the call runs: the call itself, with <see cref="ExecutionOptions.SkipValidation"/> and
/// <see cref="ExecutionOptions.BuildUp"/>, and the rerun that names an exceptional halt, as a
/// <see cref="ExecutionOptions.CommitAndRestore"/> call. Estimates, access lists, traces and simulations run on the
/// processor unwrapped.
/// </para>
/// </remarks>
public interface ICallTransactionProcessorDecorator
{
    /// <summary>Returns the processor that runs an <c>eth_call</c> in place of <paramref name="processor"/>.</summary>
    /// <param name="processor">
    /// The processor of the call's processing scope, used by the call alone until it returns. The scope may have wrapped
    /// the chain's processor in decorators of its own, so it need not be the chain's processor type.
    /// </param>
    ITransactionProcessor Decorate(ITransactionProcessor processor);
}
