// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test;

public class CustomSpecProviderTests
{
    [Test]
    public void When_no_transitions_specified_throws_argument_exception()
    {
        Assert.Throws<ArgumentException>(static () => _ = new CustomSpecProvider());
    }

    [Test]
    public void When_first_release_is_not_at_block_zero_then_throws_argument_exception()
    {
        Assert.Throws<ArgumentException>(static () => _ = new CustomSpecProvider(((ForkActivation)1, Byzantium.Instance)), "ordered");
    }

    [Test]
    public void When_only_one_release_is_specified_then_returns_that_release()
    {
        var specProvider = new CustomSpecProvider(((ForkActivation)0, Byzantium.Instance));
        Assert.That(specProvider.GetSpec((ForkActivation)0), Is.InstanceOf<Byzantium>(), "0");
        Assert.That(specProvider.GetSpec((ForkActivation)1), Is.InstanceOf<Byzantium>(), "1");
    }

    [Test]
    public void Can_find_dao_block_number()
    {
        long daoBlockNumber = 100;
        var specProvider = new CustomSpecProvider(
            ((ForkActivation)0L, Frontier.Instance),
            ((ForkActivation)daoBlockNumber, Dao.Instance));

        Assert.That(specProvider.DaoBlockNumber, Is.EqualTo(daoBlockNumber));
    }

    [Test]
    public void If_no_dao_then_no_dao_block_number()
    {
        var specProvider = new CustomSpecProvider(
            ((ForkActivation)0L, Frontier.Instance),
            ((ForkActivation)1L, Homestead.Instance));

        Assert.That(specProvider.DaoBlockNumber, Is.Null);
    }

    [Test]
    public void When_more_releases_specified_then_transitions_work()
    {
        var specProvider = new CustomSpecProvider(
            ((ForkActivation)0, Frontier.Instance),
            ((ForkActivation)1, Homestead.Instance));
        Assert.That(specProvider.GetSpec((ForkActivation)0), Is.InstanceOf<Frontier>(), "2 releases, block 0");
        Assert.That(specProvider.GetSpec((ForkActivation)1), Is.InstanceOf<Homestead>(), "2 releases, block 1");

        specProvider = new CustomSpecProvider(
            ((ForkActivation)0, Frontier.Instance),
            ((ForkActivation)1, Homestead.Instance),
            ((ForkActivation)10, Byzantium.Instance));
        Assert.That(specProvider.GetSpec((ForkActivation)0), Is.InstanceOf<Frontier>(), "3 releases, block 0");
        Assert.That(specProvider.GetSpec((ForkActivation)1), Is.InstanceOf<Homestead>(), "3 releases, block 1");
        Assert.That(specProvider.GetSpec((ForkActivation)100), Is.InstanceOf<Byzantium>(), "3 releases, block 10");
    }
}
