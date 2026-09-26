// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Scheduler;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Scheduler;

public class BackgroundTaskTypeRegistryTests
{
    /// <remarks>
    /// eth/62 and eth/66 both declare a <c>GetBlockHeadersMessage</c>, so reporting on the simple name
    /// alone would put two unrelated queues under one indistinguishable label.
    /// </remarks>
    [Test]
    public void Types_sharing_a_simple_name_are_reported_distinctly()
    {
        int first = BackgroundTaskTypeId<CollidingRequest>.Id;
        int second = BackgroundTaskTypeId<Other.CollidingRequest>.Id;

        Assert.That(first, Is.Not.EqualTo(second));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(BackgroundTaskTypeRegistry.GetName(first), Is.EqualTo(typeof(CollidingRequest).FullName));
            Assert.That(BackgroundTaskTypeRegistry.GetName(second), Is.EqualTo(typeof(Other.CollidingRequest).FullName));
        }
    }

    /// <remarks>
    /// Both the in-range slot no type has claimed, which is the branch the stats renderers rely on,
    /// and the out-of-range id that <see cref="BackgroundTaskTypeRegistry.MaxTaskTypes"/> guards.
    /// </remarks>
    [Test]
    public void Unclaimed_id_has_no_name(
        [Values(BackgroundTaskTypeRegistry.MaxTaskTypes - 1, BackgroundTaskTypeRegistry.MaxTaskTypes)] int id) =>
        Assert.That(BackgroundTaskTypeRegistry.GetName(id), Is.Null);
}

internal readonly struct CollidingRequest : IBackgroundTaskRequest<CollidingRequest>;

internal static class Other
{
    internal readonly struct CollidingRequest : IBackgroundTaskRequest<CollidingRequest>;
}
