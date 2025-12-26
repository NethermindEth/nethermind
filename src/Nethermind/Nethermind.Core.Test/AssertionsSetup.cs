// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;
using FluentAssertions;
using Nethermind.Core.Extensions;

namespace Nethermind;

/// <summary>
/// Global settings for the fluent assertions, works for the current assembly only.
/// </summary>
[SetUpFixture]
public class AssertionsSetup
{
    [OneTimeSetUp]
    public void RunBeforeAnyTests()
    {
        AssertionOptions.AssertEquivalencyUsing(static options =>
            {
                options
                    .Using<Memory<byte>>(static context =>
                        context.Subject.AsArray().Should().BeEquivalentTo(context.Expectation.AsArray()))
                    .WhenTypeIs<Memory<byte>>();
                options
                    .Using<ReadOnlyMemory<byte>>(static context =>
                        context.Subject.AsArray().Should().BeEquivalentTo(context.Expectation.AsArray()))
                    .WhenTypeIs<ReadOnlyMemory<byte>>();
                return options;
            });
    }
}
