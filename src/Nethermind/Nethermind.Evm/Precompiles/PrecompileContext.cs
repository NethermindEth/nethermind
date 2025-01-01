// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    public class PrecompileContext(IReleaseSpec spec, ISpecProvider specProvider, ExecutionEnvironment executionEnvironment)
    {
        public IReleaseSpec Spec { get; } = spec;
        public ISpecProvider SpecProvider { get; } = specProvider;
        public ExecutionEnvironment ExecutionEnvironment { get; } = executionEnvironment;
    }
}
