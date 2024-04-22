// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Evm.Test
{
    public class Eip3074Tests : VirtualMachineTestsBase
    {
        protected override ForkActivation Activation => MainnetSpecProvider.PragueActivation;
        protected override ulong Timestamp => MainnetSpecProvider.PragueBlockTimestamp;

        public static IEnumerable<object[]> AuthCases()
        {
            yield return new object[] { TestItem.PrivateKeyB, TestItem.AddressB, 0x1 };
            yield return new object[] { TestItem.PrivateKeyC, TestItem.AddressC, 0x1 };
            yield return new object[] { TestItem.PrivateKeyC, TestItem.AddressD, 0x0 };
            yield return new object[] { TestItem.PrivateKeyD, TestItem.AddressC, 0x0 };
        }

        [TestCaseSource(nameof(AuthCases))]
        public void ExecuteAuth_SignerIsSameOrDifferentThanAuthority_ReturnsOneOrZero(PrivateKey signer, Address authority, int expected)
        {
            List<byte> msg =
            [
                Eip3074Constants.AuthMagic,
                .. ((UInt256)SpecProvider.ChainId).ToBigEndian().PadLeft(32),
                .. TestState.GetNonce(signer.Address).PaddedBytes(32),
                .. SenderRecipientAndMiner.Default.Recipient.Bytes.PadLeft(32),
            ];

            byte[] commit = new byte[32];
            commit[0] = 0xff;
            msg.AddRange(commit);

            Hash256 msgDigest = Keccak.Compute(msg.ToArray());
            EthereumEcdsa ecdsa = new EthereumEcdsa(SpecProvider.ChainId, LimboLogs.Instance);

            Signature signature = ecdsa.Sign(signer, msgDigest);
            //Recovery id/yParity needs to be at index 0
            var data = signature.BytesWithRecovery[64..]
                .Concat(signature.BytesWithRecovery[..64])
                .Concat(commit).ToArray();

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

        [TestCase(true, 0)]
        [TestCase(false, 1)]
        public void ExecuteAuth_SignerNonceIsIncrementedAfterSigning_ReturnsZero(bool incrementNonce, int expected)
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

            //Simply returns the current caller
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
            commit[0] = 0xff;
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
