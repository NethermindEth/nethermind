// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using NUnit.Framework;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Data;
using Nethermind.Blockchain.Find;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    public class ForkActivationTests : TraceRpcModuleTestsBase
    {
        [Test]
        public async Task Fork_by_name_produces_valid_traces()
        {
            await SetupAsync();
            
            var result = TraceRpcModule.trace_block(BlockParameter.Latest, CreateForkParameter("prague"));
            
            AssertValidTraceResult(result);
        }

        [Test]
        public async Task Fork_by_activation_block_produces_valid_traces()
        {
            await SetupAsync();
            
            var result = TraceRpcModule.trace_block(BlockParameter.Latest, CreateForkParameter(1L));
            
            AssertValidTraceResult(result);
        }

        [Test]
        public async Task Fork_by_activation_timestamp_produces_valid_traces()
        {
            await SetupAsync();
            
            var result = TraceRpcModule.trace_block(BlockParameter.Latest, CreateForkParameter(1500000000UL));
            
            AssertValidTraceResult(result);
        }

        [Test]
        public async Task Invalid_fork_name_throws_meaningful_error()
        {
            await SetupAsync();
            
            Action act = () => TraceRpcModule.trace_block(BlockParameter.Latest, CreateForkParameter("nonexistent"));
            
            act.Should().Throw<ArgumentException>()
               .WithMessage("*Fork resolution failed*Unknown fork 'nonexistent'*");
        }

        [Test]
        public async Task Empty_fork_parameters_throws_meaningful_error()
        {
            await SetupAsync();
            
            Action act = () => TraceRpcModule.trace_block(BlockParameter.Latest, new ForkActivationParameter());
            
            act.Should().Throw<ArgumentException>()
               .WithMessage("*Fork resolution failed*Fork specification must provide*");
        }

        [Test]
        public async Task Negative_block_number_throws_meaningful_error()
        {
            await SetupAsync();
            
            Action act = () => TraceRpcModule.trace_block(BlockParameter.Latest, new ForkActivationParameter { ActivationBlock = -1 });
            
            act.Should().Throw<ArgumentException>()
               .WithMessage("*Fork resolution failed*must be non-negative*");
        }
    }
}