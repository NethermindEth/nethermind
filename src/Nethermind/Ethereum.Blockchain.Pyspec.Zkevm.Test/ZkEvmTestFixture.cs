// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Zkevm.Test;

/// <summary>
/// Base for ZkEvm blockchain tests (non-witness path).
/// Subclasses call <see cref="LoadBlockChainTests"/> or <see cref="LoadEngineBlockChainTests"/>
/// from their own <c>LoadTests()</c> to pick up the right fixture subdirectory.
/// Payloads with <c>executionWitnessMutated = true</c> are filtered out here — those are
/// authored for stateless validators and contain intentionally corrupt witnesses.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class ZkEvmBlockChainTestFixture : BlockchainTestBase
{
    [SetUp]
    public void SkipInCiOnSlowRunners() => CiRunnerGuard.SkipIfNotLinuxX64();

    protected static IEnumerable<BlockchainTest> LoadBlockChainTests(string fixtureDir) =>
        new TestsSourceLoader(Constants.Strategy, $"fixtures/blockchain_tests/{fixtureDir}")
            .LoadTests<BlockchainTest>()
            .Where(t => t.EngineNewPayloads?.Any(p => p.ExecutionWitnessMutated) != true);

    protected static IEnumerable<BlockchainTest> LoadEngineBlockChainTests(string fixtureDir) =>
        new TestsSourceLoader(Constants.Strategy, $"fixtures/blockchain_tests_engine/for_amsterdam/amsterdam/{fixtureDir}")
            .LoadTests<BlockchainTest>()
            .Where(t => t.EngineNewPayloads?.Any(p => p.ExecutionWitnessMutated) != true);
}

/// <summary>
/// Base for ZkEvm witness-validation tests (engine_newPayloadWithWitness path).
/// Extends <see cref="WitnessBlockchainTestBase"/> so the witness returned by the client
/// is compared byte-for-byte against the fixture's expected witness.
/// Filters to payloads that carry an <c>executionWitness</c> so the witness assertion
/// always fires.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class ZkEvmWitnessEngineBlockChainTestFixture : WitnessBlockchainTestBase
{
    [SetUp]
    public void SkipInCiOnSlowRunners() => CiRunnerGuard.SkipIfNotLinuxX64();

    protected static IEnumerable<BlockchainTest> LoadWitnessEngineBlockChainTests(string fixtureDir) =>
        new TestsSourceLoader(Constants.Strategy, $"fixtures/blockchain_tests_engine/for_amsterdam/amsterdam/{fixtureDir}")
            .LoadTests<BlockchainTest>()
            .Where(t => t.EngineNewPayloads?.Any(p => p.ExecutionWitnessMutated) != true)
            .Where(t => t.EngineNewPayloads?.Any(p => p.ExecutionWitness.HasValue) == true);
}
