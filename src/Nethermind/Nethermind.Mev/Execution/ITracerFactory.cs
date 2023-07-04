// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Tracing;

namespace Nethermind.Mev.Execution
{
    public interface ITracerFactory
    {
        ITracer Create();
    }
}
