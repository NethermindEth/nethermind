// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using FluentAssertions;
using Nethermind.Api.Extensions;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Api.Test
{
    public class SinglePluginLoaderTests
    {
        [Test]
        public void Can_load()
        {
            SinglePluginLoader<TestPlugin>.Instance.Load(LimboLogs.Instance);
        }

        [Test]
        public void Returns_correct_plugin()
        {
            SinglePluginLoader<TestPlugin>.Instance.PluginTypes.FirstOrDefault().Should().Be(typeof(TestPlugin));
        }
    }
}
