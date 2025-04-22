// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public class PruningTrieStateAdminModuleTests
{
    private IPruningTrieStateAdminRpcModule _adminRpcModuleModule = null!;
    private IBlockTree _blockTree = null!;
    private IVerifyTrieStarter _verifyTrieStarter = null!;
    private IStateReader _stateReader = null!;

    [SetUp]
    public void Setup()
    {
        _blockTree = Build.A.BlockTree().OfChainLength(5).TestObject;
        _verifyTrieStarter = Substitute.For<IVerifyTrieStarter>();
        _stateReader = Substitute.For<IStateReader>();

        _adminRpcModuleModule = new PruningTrieStateAdminRpcModule(
            new ManualPruningTrigger(),
            _blockTree,
            _stateReader,
            _verifyTrieStarter);
    }

    [Test]
    public async Task Test_admin_verifyTrie()
    {
        (await RpcTest.TestSerializedRequest(_adminRpcModuleModule, "admin_verifyTrie", "latest")).Should().Contain("Unable to start verify trie");
        _stateReader.HasStateForRoot(Arg.Any<Hash256>()).Returns(true);
        _verifyTrieStarter.TryStartVerifyTrie(Arg.Any<BlockHeader>()).Returns(true);
        (await RpcTest.TestSerializedRequest(_adminRpcModuleModule, "admin_verifyTrie", "latest")).Should().Contain("Starting");
    }
}
