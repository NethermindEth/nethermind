// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing;

namespace Nethermind.Blockchain.Tracing;

public interface ITracerBag
{
    void Add(IBlockTracer tracer);
    void AddRange(params IBlockTracer[] tracers);
    void Remove(IBlockTracer tracer);
}

