// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

// Parameterized methods inside ParallelScope.All fixtures do not reliably run in isolation (nunit/nunit#3371).
[TestFixture(true), TestFixture(false), NonParallelizable]
public class PayloadResendTimeoutTests(bool parallel) : BaseEngineModuleTests
{
    [TestCase(false, true, TestName = "NewPayloadV6_does_not_answer_an_empty_list_resend_from_a_queued_copy_with_a_list")]
    [TestCase(true, false, TestName = "NewPayloadV6_answers_a_same_list_resend_from_the_queued_copy")]
    public Task Payload_resend_uses_its_own_inclusion_list(bool resendSameList, bool satisfied) =>
        new EngineModuleTests(parallel).AssertPayloadResendAfterTimeout(resendSameList, satisfied);
}
