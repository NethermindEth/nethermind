// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus;

public interface IBlockProducerRunnerFactory
{
    /// <summary>
    /// Creates a <see cref="IBlockProducerRunner"/> to run the given <see cref="IBlockProducer"/>.
    /// </summary>
    /// <param name="blockProducer">
    /// The instance of <see cref="IBlockProducer"/> that should be started by the runner.
    /// </param>
    IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer);
}
