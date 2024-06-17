// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
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
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.Test;
public class Eip7702Tests : VirtualMachineTestsBase
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
        byte[] code = Prepare.EvmCode
            //Return the result of Auth
            .Op(Instruction.PUSH0)
            .Op(Instruction.MSTORE8)
            .PushSingle(1)
            .Op(Instruction.PUSH0)
            .Op(Instruction.RETURN)
            .Done;

        Signature sig = CreateContractCodeSignedMessage(signer, code);
        
        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithSetCode(new AuthorizationTuple(0, TestItem.AddressF, 0, sig))
            .WithGasLimit(40000)
            .WithGasPrice(1)
            .WithValue(0)
            .WithNonce(TestState.GetNonce(signer.Address))
            .To(TestItem.AddressB)
                .SignedAndResolved(signer)
            .TestObject;

        var result = Execute(tx);

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
                Eip7702Constants.Magic,
                new UInt256(12999999).PaddedBytes(32),
                new UInt256(0).PaddedBytes(32),
                SenderRecipientAndMiner.Default.Recipient.Bytes.PadLeft(32)
        };
        yield return new object[]
        {
                Eip7702Constants.Magic,
                new UInt256(1).PaddedBytes(32),
                new UInt256(99999999999).PaddedBytes(32),
                SenderRecipientAndMiner.Default.Recipient.Bytes.PadLeft(32)
        };
        yield return new object[]
        {
                Eip7702Constants.Magic,
                new UInt256(1).PaddedBytes(32),
                new UInt256(0).PaddedBytes(32),
                TestItem.AddressF.Bytes.PadLeft(32)
        };
    }

    [TestCaseSource(nameof(BadMessageDataCases))]
    public void ExecuteAuth_OneOfMessageArgsIsWrong_ReturnsZero(byte magicNumber, byte[] chainId, byte[] nonce, byte[] address)
    {
        PrivateKey signer = TestItem.PrivateKeyF;

        byte[] code = Prepare.EvmCode

            //Return the result of Auth
            .Op(Instruction.PUSH0)
            .Op(Instruction.MSTORE8)
            .PushSingle(1)
            .Op(Instruction.PUSH0)
            .Op(Instruction.RETURN)
            .Done;

        var data = CreateContractCodeSignedMessage(signer, code);

        var result = Execute(code);

        Assert.That(result.ReturnValue[0], Is.EqualTo(0));
    }

    private Signature CreateContractCodeSignedMessage(PrivateKey signer, byte[] contractCode)
    {
        return CreateContractCodeSignedMessage(
            signer,
            Eip7702Constants.Magic,            
            contractCode
            );
    }

    private Signature CreateContractCodeSignedMessage(PrivateKey signer, byte magicNumber, byte[] contractCode)
    {
        List<byte> msg =
        [
            magicNumber,
            .. contractCode,
        ];

        Hash256 msgDigest = Keccak.Compute(msg.ToArray());
        EthereumEcdsa ecdsa = new EthereumEcdsa(SpecProvider.ChainId, LimboLogs.Instance);

        return ecdsa.Sign(signer, msgDigest);        
    }
}

