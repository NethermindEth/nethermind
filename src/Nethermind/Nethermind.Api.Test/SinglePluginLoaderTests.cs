// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Api.Extensions;
using NUnit.Framework;

namespace Nethermind.Api.Test;

public class SinglePluginLoaderTests
{
    [Test]
    public void Can_load() => SinglePluginLoader<TestPlugin>.Instance.Load();

    [Test]
    public void Returns_correct_plugin() =>
        Assert.That(SinglePluginLoader<TestPlugin>.Instance.PluginTypes.FirstOrDefault(), Is.EqualTo(typeof(TestPlugin)));
}
