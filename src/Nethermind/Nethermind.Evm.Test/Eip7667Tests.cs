using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip7667Tests : VirtualMachineTestsBase
{
    [SetUp]
    public void SetUp()
    {
        Setup();
    }

    protected override ISpecProvider SpecProvider => new CustomSpecProvider(
        ((ForkActivation)0, Frontier.Instance),
        ((ForkActivation)1, Cancun.Instance),
        ((ForkActivation)2, Eip7667Spec.Instance));

    [Test]
    public void GetSha3Cost()
    {
        Assert.That(Cancun.Instance.GetSha3Cost(), Is.EqualTo(30));
        Assert.That(Eip7667Spec.Instance.GetSha3Cost(), Is.EqualTo(300));
    }

    [Test]
    public void GetSha3WordCost()
    {
        Assert.That(Cancun.Instance.GetSha3WordCost(), Is.EqualTo(6));
        Assert.That(Eip7667Spec.Instance.GetSha3WordCost(), Is.EqualTo(60));
    }

    [Test]
    public void GetLogDataCost()
    {
        Assert.That(Cancun.Instance.GetLogDataCost(), Is.EqualTo(8));
        Assert.That(Eip7667Spec.Instance.GetLogDataCost(), Is.EqualTo(10));
    }

    [Test]
    public void GetSha256PrecompileBaseCost()
    {
        Assert.That(Cancun.Instance.GetSha256PrecompileBaseCost(), Is.EqualTo(60));
        Assert.That(Eip7667Spec.Instance.GetSha256PrecompileBaseCost(), Is.EqualTo(300));
    }

    [Test]
    public void GetSha256PrecompileWordCost()
    {
        Assert.That(Cancun.Instance.GetSha256PrecompileWordCost(), Is.EqualTo(12));
        Assert.That(Eip7667Spec.Instance.GetSha256PrecompileWordCost(), Is.EqualTo(60));
    }

    [Test]
    public void GetBlake2GFRoundDataCost()
    {
        Assert.That(Cancun.Instance.GetBlake2GFRoundDataCost(), Is.EqualTo(1));
        Assert.That(Eip7667Spec.Instance.GetBlake2GFRoundDataCost(), Is.EqualTo(10));
    }

    [Test]
    public void Keccak256OpDifference()
    {
        byte[] code = Prepare.EvmCode
            .PushData(32)
            .PushData(0)
            .Op(Instruction.KECCAK256)
            .STOP()
            .Done;

        TestAllTracerWithOutput resultEipDisabled = Execute((ForkActivation)1, code);
        Setup();
        TestAllTracerWithOutput resultEipEnabled = Execute((ForkActivation)2, code);

        long gasDifference = Eip7667Spec.Instance.GetSha3Cost() - Cancun.Instance.GetSha3Cost()
                             + Eip7667Spec.Instance.GetSha3WordCost() - Cancun.Instance.GetSha3WordCost();

        Assert.That(resultEipEnabled.GasSpent - resultEipDisabled.GasSpent, Is.EqualTo(gasDifference));
    }

    [Test]
    public void CreateOpDifference()
    {
        byte[] salt = [4, 5, 6];
        byte[] deployedCode = [1, 2, 3];
        byte[] initCode = Prepare.EvmCode.ForInitOf(deployedCode).Done;
        byte[] createCode = Prepare.EvmCode.Create2(initCode, salt, 0).STOP().Done;

        TestAllTracerWithOutput resultEipDisabled = Execute((ForkActivation)1, DefaultBlockGasLimit, createCode);
        Setup();
        TestAllTracerWithOutput resultEipEnabled = Execute((ForkActivation)2, DefaultBlockGasLimit, createCode);

        long gasDifference = (Eip7667Spec.Instance.GetSha3WordCost() - Cancun.Instance.GetSha3WordCost())
                             * EvmPooledMemory.Div32Ceiling((UInt256)initCode.Length);

        Assert.That(resultEipEnabled.GasSpent - resultEipDisabled.GasSpent, Is.EqualTo(gasDifference));
    }

    [Test]
    public void LogOpDifference()
    {
        const int length = 32;
        byte[] createCode = Prepare.EvmCode
            .PushData(length)
            .PushData(0)
            .Op(Instruction.LOG0)
            .STOP()
            .Done;

        TestAllTracerWithOutput resultEipDisabled = Execute((ForkActivation)1, DefaultBlockGasLimit, createCode);
        Setup();
        TestAllTracerWithOutput resultEipEnabled = Execute((ForkActivation)2, DefaultBlockGasLimit, createCode);

        long gasDifference = (Eip7667Spec.Instance.GetLogDataCost() - Cancun.Instance.GetLogDataCost()) * length;

        Assert.That(resultEipEnabled.GasSpent - resultEipDisabled.GasSpent, Is.EqualTo(gasDifference));
    }

    [Test]
    public void TestSha256Precompile()
    {
        var sha256Precompile = Sha256Precompile.Instance;

        Assert.That(sha256Precompile.BaseGasCost(Eip7667Spec.Instance),
            Is.EqualTo(Eip7667Spec.Instance.GetSha256PrecompileBaseCost()));

        Assert.That(sha256Precompile.BaseGasCost(Cancun.Instance),
            Is.EqualTo(Cancun.Instance.GetSha256PrecompileBaseCost()));

        var bytes = new byte[1];
        Assert.That(sha256Precompile.DataGasCost(bytes, Eip7667Spec.Instance),
            Is.EqualTo(Eip7667Spec.Instance.GetSha256PrecompileWordCost()));

        Assert.That(sha256Precompile.DataGasCost(bytes, Cancun.Instance),
            Is.EqualTo(Cancun.Instance.GetSha256PrecompileWordCost()));
    }

    [Test]
    public void TestBlake2FPrecompile()
    {
        var blake2FPrecompile = Blake2FPrecompile.Instance;

        var bytes = new byte[213];
        bytes[3] = 1;

        Assert.That(blake2FPrecompile.DataGasCost(bytes, Eip7667Spec.Instance),
            Is.EqualTo(Eip7667Spec.Instance.GetBlake2GFRoundDataCost()));

        Assert.That(blake2FPrecompile.DataGasCost(bytes, Cancun.Instance),
            Is.EqualTo(Cancun.Instance.GetBlake2GFRoundDataCost()));
    }
}
