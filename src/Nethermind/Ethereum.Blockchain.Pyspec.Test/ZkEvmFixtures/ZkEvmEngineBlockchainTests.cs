// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test.ZkEvmFixtures;

// zkEVM witness assertion over the Engine API (blockchain_tests_engine) path: drives each engineNewPayloads[]
// entry carrying a reference executionWitness through engine_newPayloadWithWitness and compares the returned
// witness against the fixture reference. EIP-8025 executionWitnessMutated payloads instead go through plain
// engine_newPayloadV{N} and skip the compare (handled per-payload in BlockchainTestBase.RunNewPayloads).
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class ZkEvmEngineBlockchainTestFixture : PyspecLinuxX64BlockchainFixture
{
    protected ZkEvmEngineBlockchainTestFixture() : base(parallel: false, batchRead: false) { }

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

    private static IEnumerable<TestCaseData> LoadTests() =>
        PyspecLoader.ToTestCases(new TestsSourceLoader(
            new LoadPyspecTestsStrategy { ArchiveVersion = Constants.ArchiveVersion, ArchiveName = Constants.ArchiveName },
            "fixtures/blockchain_tests_engine").LoadTests<BlockchainTest>());
}

public class ZkEvmEngineBlockchainTests : ZkEvmEngineBlockchainTestFixture;
