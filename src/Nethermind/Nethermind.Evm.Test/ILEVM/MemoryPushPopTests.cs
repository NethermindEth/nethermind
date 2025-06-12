// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.CodeAnalysis.IL;
using NUnit.Framework;

namespace Nethermind.Evm.Test.ILEVM;

[TestFixture(true)]
public class MemoryPushPopTests(bool useIlEvm) : RealContractTestsBase(useIlEvm)
{
    [SetUp]
    public void SetUp()
    {
        AotContractsRepository.ClearCache();
        Precompiler.ResetEnvironment(true);

        Metrics.IlvmAotPrecompiledCalls = 0;
    }

    private static readonly byte[] Code = Prepare.EvmCode
        .PUSHx([1])
        .MSTORE(0)
        .MLOAD(0)
        .POP()
        .Done;
    protected override byte[] ByteCode => Code;
}
