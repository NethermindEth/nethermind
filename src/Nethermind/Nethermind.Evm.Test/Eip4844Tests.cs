// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Specs;
using NUnit.Framework;
using Nethermind.Int256;
using System.Linq;

namespace Nethermind.Evm.Test;

[TestFixture]
public class Eip4844Tests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.GrayGlacierBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;

    [TestCase(0, 0, Description = "Should return 0 when no hashes")]
    [TestCase(1, 1, Description = "Should return 0 when out of range")]
    [TestCase(2, 1, Description = "Should return 0 when way out of range")]
    [TestCase(0, 1, Description = "Should return hash, when exists")]
    [TestCase(1, 3, Description = "Should return hash, when exists")]
    public void Test_blobhash_index_in_range(int index, int blobhashesCount)
    {
        byte[][] hashes = new byte[blobhashesCount][];
        for (int i = 0; i < blobhashesCount; i++)
        {
            hashes[i] = new byte[32];
            for (int n = 0; n < blobhashesCount; n++)
            {
                hashes[i][n] = (byte)((i * 3 + 10 * 7) % 256);
            }
        }
        byte[] expectedOutput = blobhashesCount > index ? hashes[index] : new byte[32];

        // Cost of transaction call + PUSH1 x4 + MSTORE (entry cost + 1 memory cell used)
        const long GasCostOfCallingWrapper = GasCostOf.Transaction + GasCostOf.VeryLow * 5 + GasCostOf.Memory;

        byte[] code = Prepare.EvmCode
            .PushData(new UInt256((ulong)index))
            .BLOBHASH()
            .MSTORE(0)
            .Return(32, 0)
            .Done;

        TestAllTracerWithOutput result = Execute(Activation, 50000, code, blobVersionedHashes: hashes);

        result.StatusCode.Should().Be(StatusCode.Success);
        result.ReturnValue.SequenceEqual(expectedOutput);
        AssertGas(result, GasCostOfCallingWrapper + GasCostOf.BlobHash);
    }

    protected override TestAllTracerWithOutput CreateTracer()
    {
        TestAllTracerWithOutput tracer = base.CreateTracer();
        tracer.IsTracingAccess = false;
        return tracer;
    }
}
