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

        public static IEnumerable<object[]> AuthorityCases()
        {
            yield return new object[] { TestItem.PrivateKeyB, TestItem.AddressB, 0x1 };
            yield return new object[] { TestItem.PrivateKeyC, TestItem.AddressC, 0x1 };
            yield return new object[] { TestItem.PrivateKeyC, TestItem.AddressD, 0x0 };
            yield return new object[] { TestItem.PrivateKeyD, TestItem.AddressC, 0x0 };
        }

        [TestCaseSource(nameof(AuthorityCases))]
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

                //AUTH params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(authority)
                .Op(Instruction.AUTH)

                //Return the result of AUTH
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE8)
                .PushSingle(1)
                .Op(Instruction.PUSH0)
                .Op(Instruction.RETURN)
                .Done;

            var result = Execute(code);

            Assert.That(result.ReturnValue[0], Is.EqualTo(expected));
        }


        public static IEnumerable<object[]> CommitDataCases()
        {
            yield return new object[] { TestItem.PrivateKeyB, TestItem.AddressB, 0x1 };
            yield return new object[] { TestItem.PrivateKeyC, TestItem.AddressC, 0x1 };
            yield return new object[] { TestItem.PrivateKeyC, TestItem.AddressD, 0x0 };
            yield return new object[] { TestItem.PrivateKeyD, TestItem.AddressC, 0x0 };
        }

        [TestCaseSource(nameof(CommitDataCases))]
        public void ExecuteAuth_(PrivateKey signer, Address authority, int expected)
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

                //AUTH params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(authority)
                .Op(Instruction.AUTH)

                //Return the result of AUTH
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE8)
                .PushSingle(1)
                .Op(Instruction.PUSH0)
                .Op(Instruction.RETURN)
                .Done;

            var result = Execute(code);

            Assert.That(result.ReturnValue[0], Is.EqualTo(expected));
        }

        [TestCase(true, 0)]
        [TestCase(false, 1)]
        public void ExecuteAuth_SignerNonceIsIncrementedAfterSigning_ReturnsExpected(bool incrementNonce, int expected)
        {
            var signer = TestItem.PrivateKeyB;
            var authority = TestItem.AddressB;

            var data = CreateSignedCommitMessage(signer);

            TestState.CreateAccount(TestItem.AddressB, 1.Ether());
            if (incrementNonce)
                TestState.IncrementNonce(TestItem.AddressB);

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

                //AUTH params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(authority)
                .Op(Instruction.AUTH)

                //Return the result of AUTH
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
            var signer = TestItem.PrivateKeyB;
            var authority = TestItem.AddressB;

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

                //AUTH params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(authority)
                .Op(Instruction.AUTH)

                //Wrong authority
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(TestItem.AddressF)
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
            var signer = TestItem.PrivateKeyB;
            var authority = TestItem.AddressB;

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

                //AUTH params
                .PushSingle((UInt256)paramLength)
                .Op(Instruction.PUSH0)
                .PushData(authority)
                .Op(Instruction.AUTH)

                //Return the result of AUTH
                .Op(Instruction.PUSH0)
                .Op(Instruction.MSTORE8)
                .PushSingle(1)
                .Op(Instruction.PUSH0)
                .Op(Instruction.RETURN)
                .Done;

            var result = Execute(code);

            Assert.That(result.ReturnValue[0], Is.EqualTo(expected));
        }


        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(255)]
        public void ExecuteAuth_SignatureIsInvalid_(byte recId)
        {
            var data = new byte[97];
            data[0] = recId;

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

                //AUTH params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(TestItem.AddressA)
                .Op(Instruction.AUTH)

                //Return the result of AUTH
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
        public void ExecuteAuth_AUTHExpandsMemory_GasCostIsAUTHPlusMemoryExpansionAndColdAccountAccess(int authMemoryLength, long expectedGas)
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
        public void ExecuteAUTHCALL_TransactionReturnsTheCurrentCallerAfterAuthCall_SignerIsReturned()
        {
            var signer = TestItem.PrivateKeyB;
            var authority = TestItem.AddressB;
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

                //AUTH params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(authority)
                .Op(Instruction.AUTH)

                //Just throw away the result
                .POP()

                //AUTHCALL params
                .PushData(20)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressC)
                .PushData(1000000)
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

            Assert.That(new Address(result.ReturnValue), Is.EqualTo(TestItem.AddressB));
        }

        [Test]
        public void ExecuteAUTHCALLAndDELEGATECALL_TransactionReturnsTheCurrentCallerAfterAuthCallAndDelegateCall_ContractAddressIsReturned()
        {
            var signer = TestItem.PrivateKeyB;
            var authority = TestItem.AddressB;
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

                //AUTH params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(authority)
                .Op(Instruction.AUTH)

                //Just throw away the result
                .POP()

                //AUTHCALL params
                .PushData(20)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressC)
                .PushData(1000000)
                .Op(Instruction.AUTHCALL)
                //.PushSingle(20)
                //.PushSingle(0)
                //.Op(Instruction.RETURN)
                .Done;

            byte[] firstCallCode = Prepare.EvmCode
                .CALLER()
                .Op(Instruction.PUSH0)
                .Op(Instruction.SSTORE)
                .PushData(20)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressD)
                .PushData(1000000)
                .Op(Instruction.CALL)
                //.PushSingle(20)
                //.PushSingle(0)
                //.Op(Instruction.RETURN)
                .Done;

            byte[] secondCallCode = Prepare.EvmCode
                .CALLER()
                .Op(Instruction.PUSH0)
                .Op(Instruction.SSTORE)
                .PushData(20)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressE)
                .PushData(1000000)
                .Op(Instruction.CALL)
                //.PushSingle(20) 
                //.PushSingle(0)
                //.Op(Instruction.RETURN)
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

        [TestCase(1)]
        public void ExecuteAUTHCALL_(int expectedCost)
        {
            var signer = TestItem.PrivateKeyF;
            var authority = TestItem.AddressF;
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

                //AUTH params
                .PushSingle((UInt256)data.Length)
                .Op(Instruction.PUSH0)
                .PushData(authority)
                .Op(Instruction.AUTH)

                //Just throw away the result
                .POP()

                //AUTHCALL params
                .PushData(20)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressC)
                .PushData(1000000)
                .Done;

            var codeWithAuthCall = code.Concat(
                Prepare.EvmCode
                .Op(Instruction.AUTHCALL)
                .Done).ToArray();                

            TestState.CreateAccount(TestItem.AddressC, 0);
            TestState.InsertCode(TestItem.AddressC, Keccak.Compute(code), code, Spec);

            TestState.CreateAccount(TestItem.AddressD, 0);
            TestState.InsertCode(TestItem.AddressD, Keccak.Compute(codeWithAuthCall), codeWithAuthCall, Spec);

            var result = Execute(code);
            var resultWithAuthCall = Execute(codeWithAuthCall);

            Assert.That(resultWithAuthCall.GasSpent - result.GasSpent, Is.EqualTo(expectedCost));
        }

        private byte[] CreateSignedCommitMessage(PrivateKey signer)
        {
            List<byte> msg =
            [
                Eip3074Constants.AuthMagic,
                .. ((UInt256)SpecProvider.ChainId).ToBigEndian().PadLeft(32),
                .. TestState.GetNonce(signer.Address).PaddedBytes(32),
                .. SenderRecipientAndMiner.Default.Recipient.Bytes.PadLeft(32),
            ];

            byte[] commit = new byte[32];
            msg.AddRange(commit);

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
