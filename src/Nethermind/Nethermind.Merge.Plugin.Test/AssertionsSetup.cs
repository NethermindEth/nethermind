// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using FluentAssertions;
using Nethermind.Core;

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
        AssertionConfiguration.Current.Equivalency.Modify(static options => options.Excluding(static c => c.Name == nameof(BlockHeader.MaybeParent)));
    }
}
