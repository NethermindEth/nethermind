// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;
using NUnit.Framework.Internal;
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
        AssertionOptions.AssertEquivalencyUsing(options =>
            {
                options
                    .Using<Memory<byte>>(context =>
                        context.Subject.AsArray().Should().BeEquivalentTo(context.Expectation.AsArray()))
                    .WhenTypeIs<Memory<byte>>();
                return options;
            });
    }
}
