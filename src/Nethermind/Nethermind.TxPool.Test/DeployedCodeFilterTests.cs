using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.TxPool.Test
{
    [TestFixture]
    public class DeployedCodeFilterTests
    {
        [Test]
        public void Accept_AccountHasCode_ReturnsSenderIsContract()
        {
            IWorldState worldState = Substitute.For<IWorldState>();
            worldState.TryGetAccount(Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(x =>
            {
                x[1] = new AccountStruct(0, 0, Keccak.Zero, Keccak.MaxValue);
                return true;
            });
            ICodeInfoRepository codeInfoRepository = Substitute.For<ICodeInfoRepository>();
            IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
            specProvider.GetCurrentHeadSpec().Returns(Prague.Instance);
            Transaction transaction = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject;
            DeployedCodeFilter sut = new DeployedCodeFilter(worldState, codeInfoRepository, specProvider);
            TxFilteringState filteringState = new(transaction, worldState);

            AcceptTxResult result = sut.Accept(transaction, ref filteringState, TxHandlingOptions.None);

            Assert.That(result, Is.EqualTo(AcceptTxResult.SenderIsContract));
        }

        [Test]
        public void Accept_AccountHasDelegatedCode_ReturnsAccepted()
        {
            IWorldState worldState = Substitute.For<IWorldState>();
            worldState.TryGetAccount(Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(x =>
            {
                x[1] = new AccountStruct(0, 0, Keccak.Zero, Keccak.MaxValue);
                return true;
            });
            ICodeInfoRepository codeInfoRepository = Substitute.For<ICodeInfoRepository>();
            codeInfoRepository.IsDelegation(Arg.Any<IWorldState>(), TestItem.AddressA, out _).Returns(true);
            IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
            specProvider.GetCurrentHeadSpec().Returns(Prague.Instance);
            Transaction transaction = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject;
            DeployedCodeFilter sut = new DeployedCodeFilter(worldState, codeInfoRepository, specProvider);
            TxFilteringState filteringState = new(transaction, worldState);

            AcceptTxResult result = sut.Accept(transaction, ref filteringState, TxHandlingOptions.None);

            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
        }

        [Test]
        public void Accept_AccountHasDelegatedCodeBut7702IsNotEnabled_ReturnsSenderIsContract()
        {
            IWorldState worldState = Substitute.For<IWorldState>();
            worldState.TryGetAccount(Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(x =>
            {
                x[1] = new AccountStruct(0, 0, Keccak.Zero, Keccak.MaxValue);
                return true;
            });
            ICodeInfoRepository codeInfoRepository = Substitute.For<ICodeInfoRepository>();
            codeInfoRepository.IsDelegation(Arg.Any<IWorldState>(), TestItem.AddressA, out _).Returns(true);
            IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
            IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
            releaseSpec.IsEip3607Enabled.Returns(true);
            specProvider.GetCurrentHeadSpec().Returns(releaseSpec);
            Transaction transaction = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject;
            DeployedCodeFilter sut = new DeployedCodeFilter(worldState, codeInfoRepository, specProvider);
            TxFilteringState filteringState = new(transaction, worldState);

            AcceptTxResult result = sut.Accept(transaction, ref filteringState, TxHandlingOptions.None);

            Assert.That(result, Is.EqualTo(AcceptTxResult.SenderIsContract));
        }

        [Test]
        public void Accept_AccountHasDelegatedCodeBut3807IsNotEnabled_ReturnsAccepted()
        {
            IWorldState worldState = Substitute.For<IWorldState>();
            worldState.TryGetAccount(Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(x =>
            {
                x[1] = new AccountStruct(0, 0, Keccak.Zero, Keccak.MaxValue);
                return true;
            });
            ICodeInfoRepository codeInfoRepository = Substitute.For<ICodeInfoRepository>();
            codeInfoRepository.IsDelegation(Arg.Any<IWorldState>(), TestItem.AddressA, out _).Returns(true);
            IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
            IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
            releaseSpec.IsEip7702Enabled.Returns(true);
            specProvider.GetCurrentHeadSpec().Returns(releaseSpec);
            Transaction transaction = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject;
            DeployedCodeFilter sut = new DeployedCodeFilter(worldState, codeInfoRepository, specProvider);
            TxFilteringState filteringState = new(transaction, worldState);

            AcceptTxResult result = sut.Accept(transaction, ref filteringState, TxHandlingOptions.None);

            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
        }

        [Test]
        public void Accept_AccountHasNoCode_ReturnsAccepted()
        {
            IWorldState worldState = Substitute.For<IWorldState>();
            worldState.TryGetAccount(Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(x =>
            {
                x[1] = new AccountStruct(0, 0, Keccak.Zero, Keccak.OfAnEmptyString);
                return true;
            });
            IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
            IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
            releaseSpec.IsEip3607Enabled.Returns(true);
            releaseSpec.IsEip7702Enabled.Returns(true);
            specProvider.GetCurrentHeadSpec().Returns(releaseSpec);
            Transaction transaction = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject;
            DeployedCodeFilter sut = new DeployedCodeFilter(worldState, Substitute.For<ICodeInfoRepository>(), specProvider);
            TxFilteringState filteringState = new(transaction, worldState);

            AcceptTxResult result = sut.Accept(transaction, ref filteringState, TxHandlingOptions.None);

            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
        }
    }
}
