// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.State;

/// <summary>
/// Told about each read block processing makes past the <see cref="PreBlockCaches"/>, before the read goes to the
/// backing store; see <see cref="PreBlockCaches.ColdReadObserver"/>.
/// </summary>
/// <remarks>
/// Called inside the read, on the block-processing thread or on one of the threads it runs work on in parallel: it must
/// not throw, block or read state.
/// </remarks>
public interface IConsumerColdReadObserver
{
    void OnColdRead();
}
