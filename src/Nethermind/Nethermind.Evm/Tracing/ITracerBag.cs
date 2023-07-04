// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Tracing
{
    public interface ITracerBag
    {
        void Add(IBlockTracer tracer);
        void AddRange(params IBlockTracer[] tracers);
        void Remove(IBlockTracer tracer);
    }
}
