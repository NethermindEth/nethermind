// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Evm.Test
{
    public class Eip3074Tests : VirtualMachineTestsBase
    {
        protected override ForkActivation Activation => MainnetSpecProvider.PragueActivation;
        protected override ulong Timestamp => MainnetSpecProvider.PragueBlockTimestamp;

        protected override TestAllTracerWithOutput CreateTracer() => new() { IsTracingAccess = false };

        public static IEnumerable<object[]> AuthorityCombinationCases()
        {
            yield return new object[] { TestItem.PrivateKeyF, TestItem.AddressF, 0x1 };
            yield return new object[] { TestItem.PrivateKeyE, TestItem.AddressE, 0x1 };
            yield return new object[] { TestItem.PrivateKeyF, TestItem.AddressE, 0x0 };
            yield return new object[] { TestItem.PrivateKeyE, TestItem.AddressF, 0x0 };
        }

        [TestCaseSource(nameof(AuthorityCombinationCases))]
        public void ExecuteAuth_SignerIsSameOrDifferentThanAuthority_ReturnsOneOrZero(PrivateKey signer, Address authority, int expected)
        {
            var data = CreateSignedCommitMessage(signer);

            byte[] code = Prepare.EvmCode
                .PushData(data[..32])
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE)
                .PushData(data[32..64])
                .PushSingle(32)
                .Op(Instruction.MSTORE)
                .PushData(data[64..96])
                .PushSingle(64)
                .Op(Instruction.MSTORE)
                .PushData(data[96..])
                .PushSingle(96)
                .Op(Instruction.MSTORE)

                //Auth params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(authority)
                .Op(Instruction.AUTH)

                //Return the result of Auth
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE8)
                .PushSingle(1)
                .Op(Instruction.PUSH0)
                .Op(Instruction.RETURN)
                .Done;

            var result = Execute(code);

            Assert.That(result.ReturnValue[0], Is.EqualTo(expected));
        }

        public static IEnumerable<object[]> BadMessageDataCases()
        {
            yield return new object[]
            {
                TestContext.CurrentContext.Random.NextByte(5, byte.MaxValue),
                ((UInt256)1).ToBigEndian().PadLeft(32),
                new UInt256(0).PaddedBytes(32),
                SenderRecipientAndMiner.Default.Recipient.Bytes.PadLeft(32)
            };
            yield return new object[]
            {
                Eip3074Constants.AuthMagic,
                new UInt256(12999999).PaddedBytes(32),
                new UInt256(0).PaddedBytes(32),
                SenderRecipientAndMiner.Default.Recipient.Bytes.PadLeft(32)
            };
            yield return new object[]
            {
                Eip3074Constants.AuthMagic,
                new UInt256(1).PaddedBytes(32),
                new UInt256(99999999999).PaddedBytes(32),
                SenderRecipientAndMiner.Default.Recipient.Bytes.PadLeft(32)
            };
            yield return new object[]
            {
                Eip3074Constants.AuthMagic,
                new UInt256(1).PaddedBytes(32),
                new UInt256(0).PaddedBytes(32),
                TestItem.AddressF.Bytes.PadLeft(32)
            };
        }

        [TestCaseSource(nameof(BadMessageDataCases))]
        public void ExecuteAuth_OneOfMessageArgsIsWrong_ReturnsZero(byte magicNumber, byte[] chainId, byte[] nonce, byte[] address)
        {
            PrivateKey signer = TestItem.PrivateKeyF;
            var data = CreateSignedCommitMessage(signer, magicNumber, chainId, nonce, address, new byte[32]);

            byte[] code = Prepare.EvmCode
                .PushData(data[..32])
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE)
                .PushData(data[32..64])
                .PushSingle(32)
                .Op(Instruction.MSTORE)
                .PushData(data[64..96])
                .PushSingle(64)
                .Op(Instruction.MSTORE)
                .PushData(data[96..])
                .PushSingle(96)
                .Op(Instruction.MSTORE)

                //Auth params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(signer.Address)
                .Op(Instruction.AUTH)

                //Return the result of Auth
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE8)
                .PushSingle(1)
                .Op(Instruction.PUSH0)
                .Op(Instruction.RETURN)
                .Done;

            var result = Execute(code);

            Assert.That(result.ReturnValue[0], Is.EqualTo(0));
        }

        [Test]
        public void ExecuteAuth_CommitDataIsWrong_ReturnsZero()
        {
            PrivateKey signer = TestItem.PrivateKeyF;
            var data = CreateSignedCommitMessage(signer);
            //Start index of commit
            data[65] = 0x1;

            byte[] code = Prepare.EvmCode
                .PushData(data[..32])
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE)
                .PushData(data[32..64])
                .PushSingle(32)
                .Op(Instruction.MSTORE)
                .PushData(data[64..96])
                .PushSingle(64)
                .Op(Instruction.MSTORE)
                .PushData(data[96..])
                .PushSingle(96)
                .Op(Instruction.MSTORE)

                //Auth params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(signer.Address)
                .Op(Instruction.AUTH)

                //Return the result of Auth
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE8)
                .PushSingle(1)
                .Op(Instruction.PUSH0)
                .Op(Instruction.RETURN)
                .Done;

            var result = Execute(code);

            Assert.That(result.ReturnValue[0], Is.EqualTo(0));
        }

        [TestCase(true, 0)]
        [TestCase(false, 1)]
        public void ExecuteAuth_SignerNonceIsIncrementedAfterSigning_ReturnsExpected(bool incrementNonce, int expected)
        {
            var signer = TestItem.PrivateKeyF;
            var data = CreateSignedCommitMessage(signer);

            TestState.CreateAccount(signer.Address, 1.Ether());
            if (incrementNonce)
                TestState.IncrementNonce(signer.Address);

            byte[] code = Prepare.EvmCode
                .PushData(data[..32])
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE)
                .PushData(data[32..64])
                .PushSingle(32)
                .Op(Instruction.MSTORE)
                .PushData(data[64..96])
                .PushSingle(64)
                .Op(Instruction.MSTORE)

                //Auth params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(signer.Address)
                .Op(Instruction.AUTH)

                //Return the result of Auth
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE8)
                .PushSingle(1)
                .Op(Instruction.PUSH0)
                .Op(Instruction.RETURN)
                .Done;

            var result = Execute(code);

            Assert.That(result.ReturnValue[0], Is.EqualTo(expected));
        }

        [Test]
        public void ExecuteAuth_InvalidAuthorityAfterValidHasBeenSet_CorrectErrorIsReturned()
        {
            var signer = TestItem.PrivateKeyF;
            var data = CreateSignedCommitMessage(signer);

            byte[] code = Prepare.EvmCode
                .PushData(data[..32])
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE)
                .PushData(data[32..64])
                .PushSingle(32)
                .Op(Instruction.MSTORE)
                .PushData(data[64..96])
                .PushSingle(64)
                .Op(Instruction.MSTORE)

                //Auth params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(signer.Address)
                .Op(Instruction.AUTH)

                //Wrong authority
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(TestItem.GetRandomAddress())
                .Op(Instruction.AUTH)

                .PushData(20)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressC)
                .PushData(1000000)
                .Op(Instruction.AUTHCALL)
                .Done;

            var result = Execute(code);

            Assert.That(result.StatusCode, Is.EqualTo(0));
            Assert.That(result.Error, Is.EqualTo(EvmExceptionType.AuthorizedNotSet.ToString()));
        }

        [TestCase(66, 1)]
        [TestCase(65, 1)]
        [TestCase(256, 1)]
        [TestCase(64, 0)]
        [TestCase(0, 0)]
        public void ExecuteAuth_ParamLengthIsLessOrGreaterThanValidSignature_ReturnsExpected(int paramLength, int expected)
        {
            var signer = TestItem.PrivateKeyF;
            var data = CreateSignedCommitMessage(signer);

            byte[] code = Prepare.EvmCode
                .PushData(data[..32])
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE)
                .PushData(data[32..64])
                .PushSingle(32)
                .Op(Instruction.MSTORE)
                .PushData(data[64..96])
                .PushSingle(64)
                .Op(Instruction.MSTORE)

                //Auth params
                .PushSingle((UInt256)paramLength)
                .Op(Instruction.PUSH0)
                .PushData(signer.Address)
                .Op(Instruction.AUTH)

                //Return the result of Auth
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE8)
                .PushSingle(1)
                .Op(Instruction.PUSH0)
                .Op(Instruction.RETURN)
                .Done;

            var result = Execute(code);

            Assert.That(result.ReturnValue[0], Is.EqualTo(expected));
        }


        [Test]
        public void ExecuteAuth_SignatureIsInvalid_ReturnsZero()
        {
            var data = new byte[97];
            TestContext.CurrentContext.Random.NextBytes(data);

            byte[] code = Prepare.EvmCode
                .PushData(data[..32])
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE)
                .PushData(data[32..64])
                .PushSingle(32)
                .Op(Instruction.MSTORE)
                .PushData(data[64..96])
                .PushSingle(64)
                .Op(Instruction.MSTORE)

                //Auth params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(TestItem.AddressA)
                .Op(Instruction.AUTH)

                //Return the result of Auth
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE8)
                .PushSingle(1)
                .Op(Instruction.PUSH0)
                .Op(Instruction.RETURN)
                .Done;
            
            var result = Execute(code);

            Assert.That(result.ReturnValue[0], Is.EqualTo(0));
        }

        [TestCase(97, GasCostOf.Auth + GasCostOf.ColdAccountAccess)]
        [TestCase(160, GasCostOf.Auth + GasCostOf.Memory + GasCostOf.ColdAccountAccess)]
        [TestCase(192, GasCostOf.Auth + GasCostOf.Memory * 2 + GasCostOf.ColdAccountAccess)]
        [TestCase(193, GasCostOf.Auth + GasCostOf.Memory * 3 + GasCostOf.ColdAccountAccess)]
        public void ExecuteAuth_AuthExpandsMemory_GasCostIsAuthPlusMemoryExpansionAndColdAccountAccess(int authMemoryLength, long expectedGas)
        {
            var data = CreateSignedCommitMessage(TestItem.PrivateKeyF);

            byte[] code = Prepare.EvmCode
                .PushData(data[..32])
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE)
                .PushData(data[32..64])
                .PushSingle(32)
                .Op(Instruction.MSTORE)
                .PushData(data[64..96])
                .PushSingle(64)
                .Op(Instruction.MSTORE)
                .PushData(data[96..])
                .PushSingle(96)
                .Op(Instruction.MSTORE)

                .PushSingle((UInt256)authMemoryLength)
                .Op(Instruction.PUSH0)
                .PushData(TestItem.AddressF)
                .Done;

            var authCode =
                code.Concat(
                    Prepare.EvmCode
                    .Op(Instruction.AUTH)
                    .Done
                    ).ToArray();

            TestState.CreateAccount(TestItem.AddressC, 0);
            TestState.InsertCode(TestItem.AddressC, Keccak.Compute(code), code, Spec);

            TestState.CreateAccount(TestItem.AddressD, 0);
            TestState.InsertCode(TestItem.AddressD, Keccak.Compute(authCode), authCode, Spec);

            var resultNoAuth = Execute(code);
            var resultWithAuth = Execute(authCode);

            Assert.That(resultWithAuth.GasSpent - resultNoAuth.GasSpent, Is.EqualTo(expectedGas));
        }

        [Test]
        public void ExecuteAuthCall_TransactionReturnsTheCurrentCallerAfterAuthCall_SignerIsReturned()
        {
            var signer = TestItem.PrivateKeyF;
            var data = CreateSignedCommitMessage(signer);

            byte[] code = Prepare.EvmCode
                .PushData(data[..32])
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE)
                .PushData(data[32..64])
                .PushSingle(32)
                .Op(Instruction.MSTORE)
                .PushData(data[64..96])
                .PushSingle(64)
                .Op(Instruction.MSTORE)

                //Auth params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(signer.Address)
                .Op(Instruction.AUTH)

                //Just throw away the result
                .POP()

                //AuthCall params
                .PushData(20)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressC)
                .PushData(0)
                .Op(Instruction.AUTHCALL)
                .PushSingle(20)
                .PushSingle(0)
                .Op(Instruction.RETURN)
                .Done;

            //Simply returns the current msg.caller
            byte[] codeReturnCaller = Prepare.EvmCode
                .CALLER()
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE)
                .PushSingle(20)
                .PushSingle(12)
                .Op(Instruction.RETURN)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 0);
            TestState.InsertCode(TestItem.AddressC, Keccak.Compute(codeReturnCaller), codeReturnCaller, Spec);

            var result = Execute(code);

            Assert.That(new Address(result.ReturnValue), Is.EqualTo(signer.Address));
        }

        [Test]
        public void ExecuteAuthCallAndDELEGATECALL_TransactionReturnsTheCurrentCallerAfterAuthCallAndDelegateCall_ContractAddressIsReturned()
        {
            var signer = TestItem.PrivateKeyF;
            var data = CreateSignedCommitMessage(signer);

            byte[] code = Prepare.EvmCode
                .PushData(data[..32])
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE)
                .PushData(data[32..64])
                .PushSingle(32)
                .Op(Instruction.MSTORE)
                .PushData(data[64..96])
                .PushSingle(64)
                .Op(Instruction.MSTORE)

                //Auth params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(signer.Address)
                .Op(Instruction.AUTH)

                //Just throw away the result
                .POP()

                //AuthCall params
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressC)
                .PushData(0)
                .Op(Instruction.AUTHCALL)
                .Done;

            byte[] firstCallCode = Prepare.EvmCode
                .CALLER()
                .Op(Instruction.PUSH0)
                .Op(Instruction.SSTORE)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressD)
                .PushData(1000000)
                .Op(Instruction.CALL)
                .Done;

            byte[] secondCallCode = Prepare.EvmCode
                .CALLER()
                .Op(Instruction.PUSH0)
                .Op(Instruction.SSTORE)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressE)
                .PushData(1000000)
                .Op(Instruction.CALL)
                .Done;

            //Store caller in slot 0
            byte[] codeStoreCaller = Prepare.EvmCode
                .CALLER()
                .Op(Instruction.PUSH0)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 0);
            TestState.InsertCode(TestItem.AddressC, Keccak.Compute(firstCallCode), firstCallCode, Spec);

            TestState.CreateAccount(TestItem.AddressD, 0);
            TestState.InsertCode(TestItem.AddressD, Keccak.Compute(secondCallCode), secondCallCode, Spec);

            TestState.CreateAccount(TestItem.AddressE, 0);
            TestState.InsertCode(TestItem.AddressE, Keccak.Compute(codeStoreCaller), codeStoreCaller, Spec);

            Execute(code);

            var resultB = TestState.Get(new StorageCell(TestItem.AddressB, 0));

            Assert.That(new Address(resultB.ToArray()), Is.EqualTo(TestItem.AddressB));

            var resultD = TestState.Get(new StorageCell(TestItem.AddressD, 0));

            Assert.That(new Address(resultD.ToArray()), Is.EqualTo(TestItem.AddressC));

            var resultE = TestState.Get(new StorageCell(TestItem.AddressE, 0));

            Assert.That(new Address(resultE.ToArray()), Is.EqualTo(TestItem.AddressD));
        }

        public static IEnumerable<object[]> AuthCallGasCases()
        {
            yield return new object[]
            {
                //Cold access address
                TestItem.GetRandomAddress(),
                0,
                GasCostOf.ColdAccountAccess,
            };
            yield return new object[]
            {
                //Warm access address
                TestItem.AddressF,
                0,
                GasCostOf.WarmStateRead,
            };
            yield return new object[]
            {
                //Warm access address
                TestItem.AddressF,
                1,
                GasCostOf.AuthCallValue + GasCostOf.WarmStateRead,
            };
        }

        [TestCaseSource(nameof(AuthCallGasCases))]
        public void ExecuteAuthCall_VarmAndColdAddressesAndValueIsZeroOrGreater_UsesCorrectAmountOfGas(Address target, long valueToSend, long expectedCost)
        {
            var signer = TestItem.PrivateKeyF;
            var data = CreateSignedCommitMessage(signer);

            byte[] code = Prepare.EvmCode
                .PushData(data[..32])
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE)
                .PushData(data[32..64])
                .PushSingle(32)
                .Op(Instruction.MSTORE)
                .PushData(data[64..96])
                .PushSingle(64)
                .Op(Instruction.MSTORE)

                //Auth params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(signer.Address)
                .Op(Instruction.AUTH)

                //Just throw away the result
                .POP()

                //AuthCall params
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(valueToSend)
                .PushData(target)
                .PushData(0)
                .Done;

            var codeWithAuthCall = code.Concat(
                Prepare.EvmCode
                .Op(Instruction.AUTHCALL)
                .Done).ToArray();

            TestState.CreateAccount(signer.Address, 1.Ether());

            TestState.CreateAccount(TestItem.AddressC, 0);
            TestState.InsertCode(TestItem.AddressC, Keccak.Compute(code), code, Spec);

            TestState.CreateAccount(TestItem.AddressD, 0);
            TestState.InsertCode(TestItem.AddressD, Keccak.Compute(codeWithAuthCall), codeWithAuthCall, Spec);

            var result = Execute(code);
            var resultWithAuthCall = Execute(codeWithAuthCall);

            Assert.That(resultWithAuthCall.GasSpent - result.GasSpent, Is.EqualTo(expectedCost));
        }

        [TestCase(1000000, 30000, 30000 - GasCostOf.Gas)]
        //If gas limit is 0, all gas should be forwarded remaining after the AuthCall ops and 63/64 rule
        [TestCase(1000000, 0, 970631 - 970631 / 64 - GasCostOf.Gas)]
        [TestCase(1000000, 970631 - 970631 / 64, 970631 - 970631 / 64 - GasCostOf.Gas)]
        public void ExecuteAuthCall_GasLimitIsSetToZeroOrGreater_CorrectAmountOfGasIsForwarded(long gasLimit, long gasToSend, long expectedGasInSubcall)
        {
            var signer = TestItem.PrivateKeyF;
            var data = CreateSignedCommitMessage(signer);

            byte[] code = Prepare.EvmCode
                .PushData(data[..32])
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE)
                .PushData(data[32..64])
                .PushSingle(32)
                .Op(Instruction.MSTORE)
                .PushData(data[64..96])
                .PushSingle(64)
                .Op(Instruction.MSTORE)

                //Auth params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(signer.Address)
                .Op(Instruction.AUTH)

                //Just throw away the result
                .POP()

                //AuthCall params
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressC)
                .PushData(gasToSend)
                .Op(Instruction.AUTHCALL)
                .PushSingle(32)
                .PushSingle(0)
                .Op(Instruction.RETURN)
                .Done;

            var codeStoreGas = Prepare.EvmCode
                .GAS()
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE)
                .PushSingle(32)
                .Op(Instruction.PUSH0)
                .Op(Instruction.RETURN)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 0);
            TestState.InsertCode(TestItem.AddressC, Keccak.Compute(codeStoreGas), codeStoreGas, Spec);
            
            var result = ExecuteAndTrace(gasLimit, code);

            Assert.That(new UInt256(result.ReturnValue, true), Is.EqualTo((UInt256)expectedGasInSubcall));
        }

        [TestCase(1000000000, EvmExceptionType.OutOfGas)]
        //Set gas limit to exactly remaining gas after AuthCall + 1
        [TestCase(70631 - 70631 / 64 + 1, EvmExceptionType.OutOfGas)]
        [TestCase(70631 - 70631 / 64, null)]
        public void ExecuteAuthCall_GasLimitIsHigherThanRemainingGas_ReturnsOutOfGas(long gasLimit, EvmExceptionType? expectedErrorType)
        {
            var signer = TestItem.PrivateKeyF;
            var data = CreateSignedCommitMessage(signer);

            byte[] code = Prepare.EvmCode
                .PushData(data[..32])
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE)
                .PushData(data[32..64])
                .PushSingle(32)
                .Op(Instruction.MSTORE)
                .PushData(data[64..96])
                .PushSingle(64)
                .Op(Instruction.MSTORE)

                //Auth params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(signer.Address)
                .Op(Instruction.AUTH)

                //Just throw away the result
                .POP()

                //AuthCall params
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.GetRandomAddress())
                .PushData(gasLimit)
                .Op(Instruction.AUTHCALL)
                .Done;

            var result = Execute(code);

            Assert.That(result.Error, Is.EqualTo(expectedErrorType?.ToString()));
        }

        [Test]
        public void ExecuteAuthCall_1GweiIsSent_SignerIsDebitedAndReceiverCredited()
        {
            var signer = TestItem.PrivateKeyF;
            var data = CreateSignedCommitMessage(signer);
            Address receiver = TestItem.GetRandomAddress();
            UInt256 valueToSend = 1.GWei();
            byte[] code = Prepare.EvmCode
              .PushData(data[..32])
              .Op(Instruction.PUSH0)
              .Op(Instruction.MSTORE)
              .PushData(data[32..64])
              .PushSingle(32)
              .Op(Instruction.MSTORE)
              .PushData(data[64..96])
              .PushSingle(64)
              .Op(Instruction.MSTORE)

               //Auth params
              .PushSingle((UInt256)data.Length)
              .Op(Instruction.PUSH0)
              .PushData(signer.Address)
              .Op(Instruction.AUTH)

              //Just throw away the result
              .POP()

              //AuthCall params
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(valueToSend)
              .PushData(receiver)
              .PushData(0)
              .Op(Instruction.AUTHCALL)
              .Done;

            TestState.CreateAccount(signer.Address, 1.Ether());

            Execute(code);

            var signerBalance = TestState.GetBalance(signer.Address);
            var receiverBalance = TestState.GetBalance(receiver);

            Assert.That(signerBalance, Is.EqualTo(1.Ether() - valueToSend));
            Assert.That(receiverBalance, Is.EqualTo(valueToSend));
        }

        [Test]
        public void ExecuteAuthCall_SendingMoreThanSignerBalance_SignerBalanceIsSame()
        {
            var signer = TestItem.PrivateKeyF;
            var data = CreateSignedCommitMessage(signer);

            Address receivingAddress = TestItem.AddressC;
            byte[] code = Prepare.EvmCode
              .PushData(data[..32])
              .Op(Instruction.PUSH0)
              .Op(Instruction.MSTORE)
              .PushData(data[32..64])
              .PushSingle(32)
              .Op(Instruction.MSTORE)
              .PushData(data[64..96])
              .PushSingle(64)
              .Op(Instruction.MSTORE)

              //Auth params
              .PushSingle((UInt256)data.Length)
              .Op(Instruction.PUSH0)
              .PushData(signer.Address)
              .Op(Instruction.AUTH)

              //Just throw away the result
              .POP()

              //AuthCall params
              .PushData(20)
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(1.Ether() + 1)
              .PushData(receivingAddress)
              .PushData(1000000)
              .Op(Instruction.AUTHCALL)
              .Done;

            TestState.CreateAccount(signer.Address, 1.Ether());

            Execute(code);

            var signerBalance = TestState.GetBalance(signer.Address);
            var receiverBalance = TestState.GetBalance(receivingAddress);

            Assert.That(signerBalance, Is.EqualTo(1.Ether()));
            Assert.That(receiverBalance, Is.EqualTo((UInt256)0));
        }

        [Test]
        public void ExecuteAuthCall_SignerHasCodeDeployed_AuthorizedHasNotBeenSet()
        {
            var signer = TestItem.PrivateKeyF;
            var data = CreateSignedCommitMessage(signer);

            byte[] code = Prepare.EvmCode
              .PushData(data[..32])
              .Op(Instruction.PUSH0)
              .Op(Instruction.MSTORE)
              .PushData(data[32..64])
              .PushSingle(32)
              .Op(Instruction.MSTORE)
              .PushData(data[64..96])
              .PushSingle(64)
              .Op(Instruction.MSTORE)

              //Auth params
              .PushSingle((UInt256)data.Length)
              .Op(Instruction.PUSH0)
              .PushData(signer.Address)
              .Op(Instruction.AUTH)

              //AuthCall params
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(TestItem.GetRandomAddress())
              .PushData(0)
              .Op(Instruction.AUTHCALL)
              .Done;

            var signerCode = Prepare.EvmCode
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(signer.Address, 0);
            TestState.InsertCode(signer.Address, Keccak.Compute(signerCode), signerCode, Spec);

            var result = Execute(code);

            Assert.That(result.Error, Is.EqualTo(EvmExceptionType.AuthorizedNotSet.ToString()));
        }

        [Test]
        public void ExecuteAuthCall_AuthCallIsCalledTwice_AuthorizedIsStillSet()
        {
            var signer = TestItem.PrivateKeyF;
            var data = CreateSignedCommitMessage(signer);

            byte[] code = Prepare.EvmCode
              .PushData(data[..32])
              .Op(Instruction.PUSH0)
              .Op(Instruction.MSTORE)
              .PushData(data[32..64])
              .PushSingle(32)
              .Op(Instruction.MSTORE)
              .PushData(data[64..96])
              .PushSingle(64)
              .Op(Instruction.MSTORE)

              //Auth params
              .PushSingle((UInt256)data.Length)
              .Op(Instruction.PUSH0)
              .PushData(signer.Address)
              .Op(Instruction.AUTH)

              //AuthCall params
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(TestItem.GetRandomAddress())
              .PushData(0)
              .Op(Instruction.AUTHCALL)
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(TestItem.GetRandomAddress())
              .PushData(0)
              .Op(Instruction.AUTHCALL)
              .Done;

            var result = Execute(code);

            Assert.That(result.Error, Is.EqualTo(null));
        }

        [Test]
        public void ExecuteAuthCall_AuthorizedIsSetAndAuthCallIsCalledInSubcall_AuthorizedIsNotSetInSubcall()
        {
            var signer = TestItem.PrivateKeyF;
            var data = CreateSignedCommitMessage(signer);

            byte[] code = Prepare.EvmCode
              .PushData(data[..32])
              .Op(Instruction.PUSH0)
              .Op(Instruction.MSTORE)
              .PushData(data[32..64])
              .PushSingle(32)
              .Op(Instruction.MSTORE)
              .PushData(data[64..96])
              .PushSingle(64)
              .Op(Instruction.MSTORE)

              //Auth params
              .PushSingle((UInt256)data.Length)
              .Op(Instruction.PUSH0)
              .PushData(signer.Address)
              .Op(Instruction.AUTH)
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(TestItem.AddressC)
              .PushData(10000)
              .Op(Instruction.CALL)
              .Done;

            var authCall = Prepare.EvmCode
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(0)
              .PushData(TestItem.GetRandomAddress())
              .PushData(0)
              .Op(Instruction.AUTHCALL)
              .Done;

            TestState.CreateAccount(TestItem.AddressC, 0);
            TestState.InsertCode(TestItem.AddressC, Keccak.Compute(authCall), authCall, Spec);

            var result = ExecuteAndTrace(code);
            Assert.That(result.Entries.Last(e=> e.Opcode.Equals(Instruction.AUTHCALL.ToString())).Error, Is.Not.Null);
        }

        private byte[] CreateSignedCommitMessage(PrivateKey signer)
        {
            return CreateSignedCommitMessage(
                signer,
                Eip3074Constants.AuthMagic,
                ((UInt256)SpecProvider.ChainId).ToBigEndian().PadLeft(32),
                TestState.GetNonce(signer.Address).PaddedBytes(32),
                SenderRecipientAndMiner.Default.Recipient.Bytes.PadLeft(32),
                new byte[32]
                );
        }

        private byte[] CreateSignedCommitMessage(PrivateKey signer, byte magicNumber, byte[] chainId, byte[] nonce, byte[] address, byte[] commit)
        {
            List<byte> msg =
            [
                magicNumber,
                .. chainId,
                .. nonce,
                .. address,
                .. commit
            ];

            Hash256 msgDigest = Keccak.Compute(msg.ToArray());
            EthereumEcdsa ecdsa = new EthereumEcdsa(SpecProvider.ChainId, LimboLogs.Instance);

            Signature signature = ecdsa.Sign(signer, msgDigest);
            //Recovery id/yParity needs to be at index 0
            var data = signature.BytesWithRecovery[64..]
                .Concat(signature.BytesWithRecovery[..64])
                .Concat(commit).ToArray();
            return data;
        }
    }
}
