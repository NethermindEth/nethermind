// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test.ZkEvmFixtures;

// zkEVM witness assertion over the Engine API (blockchain_tests_engine) path. Per-payload dispatch between
// engine_newPayloadWithWitnessV* and the plain endpoint lives in BlockchainTestBase.RunNewPayloads.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class ZkEvmEngineBlockchainTestFixture : PyspecLinuxX64BlockchainFixture
{
    protected ZkEvmEngineBlockchainTestFixture() : base(parallel: false, batchRead: false) { }

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(PyspecTestRef testRef) => Assert.That((await RunTest(PyspecLoader.LoadTest<BlockchainTest>(testRef))).Pass, Is.True);

    private static IEnumerable<TestCaseData> LoadTests() =>
        PyspecLoader.LoadCases<BlockchainTest>(
            new LoadPyspecTestsStrategy { ArchiveVersion = Constants.ArchiveVersion, ArchiveName = Constants.ArchiveName },
            "fixtures/blockchain_tests_engine");
}

public class ZkEvmEngineBlockchainTests : ZkEvmEngineBlockchainTestFixture;
