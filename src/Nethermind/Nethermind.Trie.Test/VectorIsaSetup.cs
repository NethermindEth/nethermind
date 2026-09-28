// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

/// <summary>
/// Fails this assembly's run when it was pinned to an instruction set it did not get.
/// </summary>
/// <remarks>
/// The batch Keccak kernels behind branch encoding are four lanes wide without AVX-512 and eight
/// with it, and the walk that defers a child to them differs too, so each run covers only one of
/// the two. NUnit finds a set-up fixture in the assembly under test, which is why this sits here
/// rather than being inherited.
/// </remarks>
[SetUpFixture]
public class VectorIsaSetup
{
    [OneTimeSetUp]
    public void Check_instruction_set_expectations() => VectorIsaExpectations.AssertPinnedInstructionSet();
}
