// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class VerkleTreeTests: VirtualMachineTestsBase
{
    public VerkleTreeTests() : base(StateType.Verkle) { }

    protected override long BlockNumber => MainnetSpecProvider.GrayGlacierBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.PragueBlockTimestamp;

    [Test]
    public void TestGasCostUpdateForPushOpCodes()
    {

    }
    [Test]
    public void TestGasCostUpdateForContractCreationComplete()
    {

    }

    [Test]
    public void TestGasCostUpdateForContractCreationInit()
    {

    }
    [Test]
    public void TestGasCostUpdateForBalanceOpCode()
    {

    }
    [Test]
    public void TestGasCostUpdateForCodeOpCodes()
    {

    }
    [Test]
    public void TestGasCostUpdateForStorageAccessAndUpdate()
    {

    }
    [Test]
    public void TestGasCostForProofOfAbsence()
    {

    }
    [Test]
    public void TestGasCostUpdateForCodeChunksAccess()
    {

    }
    [Test]
    public void TestGasCostUpdateForCodeCopy()
    {

    }
    [Test]
    public void TestGasCostUpdateForPush1()
    {

    }
    [Test]
    public void TestGasCostUpdateForPushX()
    {

    }
    [Test]
    public void TestGasCostUpdateForCreateOpCodes()
    {

    }

}
