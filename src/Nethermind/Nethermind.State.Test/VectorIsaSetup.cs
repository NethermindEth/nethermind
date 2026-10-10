// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test;
using NUnit.Framework;

namespace Nethermind.Store.Test;

/// <summary>
/// Fails this assembly's run when it was pinned to an instruction set it did not get.
/// </summary>
/// <remarks>
/// The queued account key hashes go to a kernel four lanes wide without AVX-512 and eight with it,
/// and the queue holds up to eight either way, so only a run without AVX-512 covers a count that
/// exceeds the kernel's width. NUnit finds a set-up fixture in the assembly under test, which is
/// why this sits here rather than being inherited.
/// </remarks>
[SetUpFixture]
public class VectorIsaSetup
{
    [OneTimeSetUp]
    public void Check_instruction_set_expectations() => VectorIsaExpectations.AssertPinnedInstructionSet();
}
