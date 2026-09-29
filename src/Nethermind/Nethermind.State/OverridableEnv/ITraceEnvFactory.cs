// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.OverridableEnv;

/// <summary>Builds the environments historical traces run in: unlike <see cref="IOverridableEnvFactory"/>, their
/// reads can be seeded with the state a block's earlier transactions left, so a trace skips replaying them. Every other
/// read-only environment stays without the overlay and pays nothing for it.</summary>
public interface ITraceEnvFactory
{
    IOverridableEnv CreateForTracing();
}
