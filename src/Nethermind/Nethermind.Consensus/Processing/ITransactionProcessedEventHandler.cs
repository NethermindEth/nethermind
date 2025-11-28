// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Used by <see cref="FilterManager"/> through <see cref="IMainProcessingContext"/>
/// </summary>
public interface ITransactionProcessedEventHandler
{
    void OnTransactionProcessed(TxProcessedEventArgs txProcessedEventArgs);
}
