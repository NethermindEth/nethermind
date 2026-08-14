// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Producers;

/// <summary>
/// A <see cref="IBlockProducerEnvFactory"/> whose environments execute on the global (main)
/// world state, so produced state is written through to the state backend instead of being
/// discarded with a detached producer state. Required by consumers that make produced blocks
/// canonical without re-processing them, e.g. <c>testing_commitBlockV1</c>.
/// </summary>
public interface IMainStateBlockProducerEnvFactory : IBlockProducerEnvFactory;
