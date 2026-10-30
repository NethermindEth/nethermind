// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Follower.RollupX;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXGenesisTests
{
    public class NativeFollowerStateRecoveryTests
    {
        private NativeHistoryTestSource _source = null!;
        private IContainer _container = null!;
        private IBlockTree _tree = null!;
        private IStateReader _state = null!;
        private IBlockProcessingQueue _queue = null!;
        private IBlockValidator _validator = null!;
        private IDerivedHeaderRules _headers = null!;
        private IUnsafeBlockPreparation _preparation = null!;
        private ReceiptConfig _receipts = null!;
        private NativeFollowerStateRecovery _recovery = null!;
        private readonly HashSet<Hash256> _available = [];
        private readonly List<ulong> _restored = [];

        [SetUp]
        public void SetUp()
        {
            _available.Clear();
            _restored.Clear();
            _tree = Build.A.BlockTree().OfChainLength(66).TestObject;
            _available.Add(_tree.Genesis!.Hash!);
            _state = Substitute.For<IStateReader>();
            _state.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(call => _available.Contains(call.ArgAt<BlockHeader>(0).Hash!));
            _queue = Substitute.For<IBlockProcessingQueue>();
            _queue.IsEmpty.Returns(true);
            _queue.Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>()).Returns(call =>
            {
                Block block = call.ArgAt<Block>(0);
                _restored.Add(block.Number);
                _available.Add(block.Hash!);
                _queue.BlockRemoved += Raise.EventWith(new BlockRemovedEventArgs(block.Hash!, ProcessingResult.Success));
                return ValueTask.CompletedTask;
            });
            _validator = Substitute.For<IBlockValidator>();
            _validator.ValidateSuggestedBlock(Arg.Any<Block>(), Arg.Any<BlockHeader>(), out _, true).Returns(true);
            _headers = Substitute.For<IDerivedHeaderRules>();
            _headers.Mismatch(Arg.Any<Block>(), Arg.Any<BlockHeader>()).Returns((string?)null);
            _preparation = Substitute.For<IUnsafeBlockPreparation>();
            _preparation.Prepare(Arg.Any<BlockHeader>(), Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(UnsafeBlockPreparationResult.Ready);
            _receipts = new ReceiptConfig { StoreReceipts = true };
            _source = CreateHistorySource();
            _container = Container(_tree);
            _recovery = (NativeFollowerStateRecovery)_container.Resolve<IFollowerStateRecovery>();
        }

        [TearDown]
        public void TearDown()
        {
            _container.Dispose();
            _source.Dispose();
        }

        [Test]
        public async Task Restore_AvailableCheckpoint_LeavesProcessingUntouched()
        {
            await _recovery.Restore(_tree.Genesis!, CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_queue.ReceivedCalls(), Is.Empty);
                Assert.That(_preparation.ReceivedCalls(), Is.Empty);
            }
        }

        [Test]
        public async Task Restore_MissingState_ValidatesAndReplaysOnlyTheMissingPrefix([Values] bool storeReceipts)
        {
            _receipts.StoreReceipts = storeReceipts;
            _available.Add(Header(2).Hash!);
            Hash256 originalHead = _tree.Head!.Hash!;

            await _recovery.Restore(Header(5), CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_restored, Is.EqualTo(new ulong[] { 3, 4, 5 }));
                Assert.That(_tree.Head.Hash, Is.EqualTo(originalHead), "state recovery must not publish an intermediate head");
                Assert.That(_available, Does.Contain(Header(5).Hash));
                foreach (ulong number in _restored)
                {
                    Block block = _tree.FindBlock(number)!;
                    _validator.Received(1).ValidateSuggestedBlock(block, Header(number - 1), out _, true);
                    await _preparation.Received(1).Prepare(Header(number - 1), block, CancellationToken.None);
                    await _queue.Received(1).Enqueue(block, ProcessingOptions.ForceProcessing | ProcessingOptions.EthereumMerge |
                        (storeReceipts ? ProcessingOptions.StoreReceipts : ProcessingOptions.None));
                    await _queue.Received(1).WaitUntilRemovedAsync(block.Hash!);
                }
            }
        }

        [Test]
        public async Task Restore_LargeGap_KeepsProgressAcrossBoundedAttempts()
        {
            await Assert.ThrowsAsync<EezEngineUnavailableException>(() => _recovery.Restore(Header(65), CancellationToken.None));
            Assert.That(_restored.Count, Is.EqualTo(32));
            await Assert.ThrowsAsync<EezEngineUnavailableException>(() => _recovery.Restore(Header(65), CancellationToken.None));
            Assert.That(_restored.Count, Is.EqualTo(64));

            await _recovery.Restore(Header(65), CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_restored.Count, Is.EqualTo(65));
                Assert.That(new HashSet<ulong>(_restored).Count, Is.EqualTo(65), "a retry must not repeat already restored blocks");
            }
        }

        [Test]
        public async Task Restore_CancellationAfterEnqueue_DrainsTheBlockBeforeReturning()
        {
            Block block = _tree.FindBlock(1UL)!;
            using CancellationTokenSource cancellation = new();
            TaskCompletionSource removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>()).Returns(ValueTask.CompletedTask);
            _queue.WaitUntilRemovedAsync(block.Hash!).Returns(_ => new ValueTask(removed.Task));
            Task restore = _recovery.Restore(Header(2), cancellation.Token);
            cancellation.Cancel();
            Assert.That(restore.IsCompleted, Is.False, "queued work still owns its prepared L1 evidence");

            _available.Add(block.Hash!);
            _queue.BlockRemoved += Raise.EventWith(new BlockRemovedEventArgs(block.Hash!, ProcessingResult.Success));
            removed.SetResult();

            await Assert.ThrowsAsync<OperationCanceledException>(() => restore);
            await _preparation.Received(1).Prepare(Arg.Any<BlockHeader>(), Arg.Any<Block>(), Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task Restore_QueueDropsWorkWithoutVerdict_RetriesWithoutWaitingForAnEvent()
        {
            _queue.Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>()).Returns(ValueTask.CompletedTask);

            await Assert.ThrowsAsync<EezEngineUnavailableException>(() => _recovery.Restore(Header(1), CancellationToken.None));

            Assert.That(_available, Does.Not.Contain(Header(1).Hash));
        }

        [Test]
        public async Task Restore_UncommittedState_DoesNotAdvancePastTheBlock([Values(ProcessingResult.Success, ProcessingResult.Exception)] ProcessingResult result)
        {
            _queue.Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>()).Returns(call =>
            {
                _queue.BlockRemoved += Raise.EventWith(new BlockRemovedEventArgs(call.ArgAt<Block>(0).Hash!, result));
                return ValueTask.CompletedTask;
            });

            await Assert.ThrowsAsync<EezEngineUnavailableException>(() => _recovery.Restore(Header(2), CancellationToken.None));

            await _preparation.Received(1).Prepare(Arg.Any<BlockHeader>(), Arg.Any<Block>(), Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task Restore_NativeEvidenceDisagrees_DoesNotEnqueue([Values(UnsafeBlockPreparationResult.ParentReorganized, UnsafeBlockPreparationResult.SourceNotCanonical)] UnsafeBlockPreparationResult result)
        {
            _preparation.Prepare(Arg.Any<BlockHeader>(), Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(result);

            await Assert.ThrowsAsync<L1SourceIncompleteException>(() => _recovery.Restore(Header(1), CancellationToken.None));

            await _queue.DidNotReceiveWithAnyArgs().Enqueue(default!, default);
        }

        [Test]
        public async Task Restore_InvalidSuggestedBlock_DoesNotEnqueue()
        {
            _validator.ValidateSuggestedBlock(Arg.Any<Block>(), Arg.Any<BlockHeader>(), out _, true).Returns(false);

            await Assert.ThrowsAsync<EezFollowerException>(() => _recovery.Restore(Header(1), CancellationToken.None));

            await _queue.DidNotReceiveWithAnyArgs().Enqueue(default!, default);
        }

        [Test]
        public async Task Restore_StateExistsWithoutMatchingSuccessfulVerdict_DoesNotAcceptIt([Values] bool wrongHash)
        {
            _queue.Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>()).Returns(call =>
            {
                Block block = call.ArgAt<Block>(0);
                _available.Add(block.Hash!);
                _queue.BlockRemoved += Raise.EventWith(new BlockRemovedEventArgs(wrongHash ? Header(2).Hash! : block.Hash!,
                    wrongHash ? ProcessingResult.Success : ProcessingResult.Exception));
                return ValueTask.CompletedTask;
            });

            await Assert.ThrowsAsync<EezEngineUnavailableException>(() => _recovery.Restore(Header(1), CancellationToken.None));
        }

        [Test]
        public async Task Restore_DeepMissingState_BoundsAndContinuesTheAncestorSearch()
        {
            IBlockTree tree = Build.A.BlockTree().OfChainLength(UnsafeHeadFollower.MaxDepth + 3).TestObject;
            _available.Clear();
            _available.Add(tree.Genesis!.Hash!);
            using IContainer container = Container(tree);
            IFollowerStateRecovery recovery = container.Resolve<IFollowerStateRecovery>();
            BlockHeader endpoint = tree.Head!.Header;

            await Assert.ThrowsAsync<EezEngineUnavailableException>(() => recovery.Restore(endpoint, CancellationToken.None));
            Assert.That(_restored, Is.Empty, "the first attempt only searches the bounded ancestry window");

            await Assert.ThrowsAsync<EezEngineUnavailableException>(() => recovery.Restore(endpoint, CancellationToken.None));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_restored.Count, Is.EqualTo(32));
                Assert.That(_restored[0], Is.EqualTo(1), "the second attempt continues the search rather than starting it again");
            }
        }

        private IContainer Container(IBlockTree tree) => NativeFollowerContainer(_source, FollowerRules(_source), configure: builder =>
            builder.AddSingleton(tree)
                .AddSingleton(_state)
                .AddSingleton(_queue)
                .AddSingleton(_validator)
                .AddSingleton(_headers)
                .AddSingleton(_preparation)
                .AddSingleton<IReceiptConfig>(_receipts));

        private BlockHeader Header(ulong number) => _tree.FindHeader(number)!;
    }
}
