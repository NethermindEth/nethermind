// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using NSubstitute;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Hive.Tests
{
    public class HivePluginTests
    {
        [Test]
        public void Can_create()
        {
            _ = new HivePlugin(new HiveConfig() { Enabled = true });
        }

        [Test]
        public void Can_initialize()
        {
            INethermindPlugin plugin = new HivePlugin(new HiveConfig() { Enabled = true });
            plugin.Init(Runner.Test.Ethereum.Build.ContextWithMocks());
            plugin.InitRpcModules();
        }

        [Test]
        public void Can_resolve_hive_step()
        {
            using IContainer container = new ContainerBuilder()
                .AddModule(new TestNethermindModule())
                .AddModule(new HiveModule())
                .Build();

            container.Resolve<HiveStep>();
        }

        [Test]
        public async Task Invalid_block_in_blocks_dir_does_not_become_parent_for_following_block()
        {
            Block genesis = Build.A.Block.Genesis.TestObject;
            Block invalidBlock = Build.A.Block
                .WithParent(genesis.Header)
                .WithExtraData([0x01])
                .TestObject;
            Block validSibling = Build.A.Block
                .WithParent(genesis.Header)
                .WithExtraData([0x02])
                .WithTimestamp(genesis.Header.Timestamp + 2)
                .TestObject;

            using TempPath blocksDir = TempPath.GetTempDirectory(Path.Combine(nameof(HivePluginTests), Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(blocksDir.Path);

            File.WriteAllBytes(Path.Combine(blocksDir.Path, "0001.rlp"), Rlp.Encode(invalidBlock).Bytes);
            File.WriteAllBytes(Path.Combine(blocksDir.Path, "0002.rlp"), Rlp.Encode(validSibling).Bytes);

            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IBlockProcessingQueue blockProcessingQueue = Substitute.For<IBlockProcessingQueue>();
            IBlockValidator blockValidator = Substitute.For<IBlockValidator>();
            IFileSystem fileSystem = Substitute.For<IFileSystem>();

            blockTree.Genesis.Returns(genesis.Header);
            blockTree.SuggestBlockAsync(Arg.Any<Block>(), Arg.Any<BlockTreeSuggestOptions>())
                .Returns(new ValueTask<AddBlockResult>(AddBlockResult.AlreadyKnown));
            fileSystem.File.Exists(Arg.Any<string>()).Returns(false);

            blockValidator
                .ValidateSuggestedBlock(Arg.Any<Block>(), Arg.Any<BlockHeader>(), out Arg.Any<string>())
                .Returns(callInfo =>
                {
                    Block block = callInfo.ArgAt<Block>(0);
                    bool isValid = block.Hash == validSibling.Hash;
                    callInfo[2] = isValid ? null : "invalid";
                    return isValid;
                });

            HiveRunner hiveRunner = new(
                blockTree,
                blockProcessingQueue,
                new HiveConfig
                {
                    BlocksDir = blocksDir.Path,
                    ChainFile = Path.Combine(blocksDir.Path, "missing.rlp"),
                },
                LimboLogs.Instance,
                fileSystem,
                blockValidator);

            await hiveRunner.Start(CancellationToken.None);

            Received.InOrder(() =>
            {
                blockValidator.ValidateSuggestedBlock(
                    Arg.Is<Block>(b => b.Hash == invalidBlock.Hash),
                    Arg.Is<BlockHeader>(h => h.Hash == genesis.Header.Hash),
                    out Arg.Any<string>());
                blockValidator.ValidateSuggestedBlock(
                    Arg.Is<Block>(b => b.Hash == validSibling.Hash),
                    Arg.Is<BlockHeader>(h => h.Hash == genesis.Header.Hash),
                    out Arg.Any<string>());
            });

            _ = blockTree.DidNotReceive()
                .SuggestBlockAsync(Arg.Is<Block>(b => b.Hash == invalidBlock.Hash), Arg.Any<BlockTreeSuggestOptions>());
            _ = blockTree.Received(1)
                .SuggestBlockAsync(Arg.Is<Block>(b => b.Hash == validSibling.Hash), Arg.Any<BlockTreeSuggestOptions>());
        }
    }
}
