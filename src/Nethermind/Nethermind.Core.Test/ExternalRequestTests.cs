// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[TestFixture]
public class ExternalRequestTests
{
    [Test]
    public void Entering_a_request_lasts_until_its_scope_ends_and_restores_the_one_before()
    {
        long outer = ExternalRequest.NewId();
        long inner = ExternalRequest.NewId();
        long beforeOuter = ExternalRequest.Current;
        long duringOuter, duringInner, afterInner, onAnotherThread = -1;
        using (ExternalRequest.Enter(outer))
        {
            duringOuter = ExternalRequest.Current;
            using (ExternalRequest.Enter(inner))
            {
                duringInner = ExternalRequest.Current;
                Thread thread = new(() => onAnotherThread = ExternalRequest.Current);
                thread.Start();
                thread.Join();
            }

            afterInner = ExternalRequest.Current;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outer, Is.Not.EqualTo(ExternalRequest.None).And.Not.EqualTo(inner), "new ids are distinct and never None");
            Assert.That(beforeOuter, Is.EqualTo(ExternalRequest.None));
            Assert.That(duringOuter, Is.EqualTo(outer));
            Assert.That(duringInner, Is.EqualTo(inner));
            Assert.That(afterInner, Is.EqualTo(outer), "leaving the inner request restores the outer one");
            Assert.That(ExternalRequest.Current, Is.EqualTo(ExternalRequest.None), "leaving the outer request restores none");
            Assert.That(onAnotherThread, Is.EqualTo(ExternalRequest.None), "the request is the thread's own");
        }
    }
}
