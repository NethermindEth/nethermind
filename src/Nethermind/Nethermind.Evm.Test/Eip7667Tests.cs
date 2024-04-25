using FluentAssertions;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip7667Tests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.PragueBlockTimestamp;

    [Test]
    public void Sha3_cost_before_eip_7667()
    {
        var spec = SpecProvider.GetSpec((BlockNumber, Timestamp - 1));
        Assert.That(spec.GetSha3Cost(), Is.EqualTo(30));
    }
    
    [Test]
    public void Sha3_cost_after_eip_7667()
    {
        Assert.That(Spec.GetSha3Cost(), Is.EqualTo(300));
    }

    [Test]
    public void Sha3_word_cost_before_eip_7667()
    {
        var spec = SpecProvider.GetSpec((BlockNumber, Timestamp - 1));
        Assert.That(spec.GetSha3WordCost(), Is.EqualTo(6));
    }

    [Test]
    public void Sha3_word_cost_after_eip_7667()
    {
        Assert.That(Spec.GetSha3WordCost(), Is.EqualTo(60));
    }

    [Test]
    public void Log_data_cost_before_eip_7667()
    {
        var spec = SpecProvider.GetSpec((BlockNumber, Timestamp - 1));
        Assert.That(spec.GetLogDataCost(), Is.EqualTo(8));
    }

    [Test]
    public void Log_data_cost_after_eip_7667()
    {
        Assert.That(Spec.GetLogDataCost(), Is.EqualTo(10));
    }

    [Test]
    public void Keccak256_op_before_eip_7667()
    {
        byte[] code = Prepare.EvmCode
            .PushData(32)
            .PushData(0)
            .Op(Instruction.KECCAK256)
            .STOP()
            .Done;

        TestAllTracerWithOutput result = Execute((BlockNumber, Timestamp - 1), code);

        long gasSpent = GasCostOf.Transaction + GasCostOf.VeryLow * 2 // for push
                                             + GasCostOf.VeryLow // memory gas cost
                                             + GasCostOf.Sha3 + GasCostOf.Sha3Word; // Keccak256

        result.StatusCode.Should().Be(1);
        Assert.That(result.GasSpent, Is.EqualTo(gasSpent));
    }

    [Test]
    public void Keccak256_op_after_eip_7667()
    {
        byte[] code = Prepare.EvmCode
            .PushData(32)
            .PushData(0)
            .Op(Instruction.KECCAK256)
            .STOP()
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        long gasSpent = GasCostOf.Transaction + GasCostOf.VeryLow * 2 // for push
                                              + GasCostOf.VeryLow // memory gas cost
                                              + GasCostOf.Sha3Eip7667 + GasCostOf.Sha3WordEip7667; // Keccak256

        result.StatusCode.Should().Be(1);
        Assert.That(result.GasSpent, Is.EqualTo(gasSpent));
    }

    [Test]
    public void Create_op_before_eip_7667()
    {
        byte[] salt = [4, 5, 6];
        byte[] deployedCode = [1, 2, 3];
        byte[] initCode = Prepare.EvmCode.ForInitOf(deployedCode).Done;
        byte[] createCode = Prepare.EvmCode.Create2(initCode, salt, 0).STOP().Done;

        TestAllTracerWithOutput result = Execute((BlockNumber, Timestamp - 1), createCode);

        long gasSpent = GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling((UInt256)initCode.Length) + 53658;

        Assert.That(result.GasSpent, Is.EqualTo(gasSpent));
    }
    
    [Test]
    public void Create_op_after_eip_7667()
    {
        byte[] salt = [4, 5, 6];
        byte[] deployedCode = [1, 2, 3];
        byte[] initCode = Prepare.EvmCode.ForInitOf(deployedCode).Done;
        byte[] createCode = Prepare.EvmCode.Create2(initCode, salt, 0).STOP().Done;

        TestAllTracerWithOutput result = Execute(createCode);

        long gasSpent = GasCostOf.Sha3WordEip7667 * EvmPooledMemory.Div32Ceiling((UInt256)initCode.Length) + 53658;

        Assert.That(result.GasSpent, Is.EqualTo(gasSpent));
    }

    [Test]
    public void Log_op_before_eip7667()
    {
        const int length = 32;
        byte[] createCode = Prepare.EvmCode
            .PushData(length)
            .PushData(0)
            .Op(Instruction.LOG0)
            .STOP()
            .Done;

        TestAllTracerWithOutput result = Execute((BlockNumber, Timestamp - 1), createCode);

        long gasSpent = GasCostOf.LogData * length + 21384;

        Assert.That(result.GasSpent, Is.EqualTo(gasSpent));
    }
    
    [Test]
    public void Log_op_after_eip7667()
    {
        const int length = 32;
        byte[] createCode = Prepare.EvmCode
            .PushData(length)
            .PushData(0)
            .Op(Instruction.LOG0)
            .STOP()
            .Done;

        TestAllTracerWithOutput result = Execute(createCode);

        long gasSpent = GasCostOf.LogDataEip7667 * length + 21384;

        Assert.That(result.GasSpent, Is.EqualTo(gasSpent));
    }

    [Test]
    public void Sha256_precompile_base_cost_before_eip_7667()
    {
        var sha256Precompile = Sha256Precompile.Instance;
        var spec = SpecProvider.GetSpec((BlockNumber, Timestamp - 1));

        Assert.That(sha256Precompile.BaseGasCost(spec), Is.EqualTo(GasCostOf.Sha256PrecompileBaseCost));
        Assert.That(GasCostOf.Sha256PrecompileBaseCost, Is.EqualTo(60));
    }
    
    [Test]
    public void Sha256_precompile_base_cost_after_eip_7667()
    {
        var sha256Precompile = Sha256Precompile.Instance;

        Assert.That(sha256Precompile.BaseGasCost(Spec), Is.EqualTo(GasCostOf.Sha256PrecompileBaseCostEip7667));
        Assert.That(GasCostOf.Sha256PrecompileBaseCostEip7667, Is.EqualTo(300));
    }
    
    [Test]
    public void Sha256_precompile_data_cost_before_eip_7667()
    {
        var sha256Precompile = Sha256Precompile.Instance;
        var bytes = new byte[1];
        var spec = SpecProvider.GetSpec((BlockNumber, Timestamp - 1));
        var dataCost = GasCostOf.Sha256PrecompileWordCost * EvmPooledMemory.Div32Ceiling((ulong)bytes.Length);

        Assert.That(sha256Precompile.DataGasCost(bytes, spec), Is.EqualTo(dataCost));
        Assert.That(GasCostOf.Sha256PrecompileWordCost, Is.EqualTo(12));
    }
    
    [Test]
    public void Sha256_precompile_data_cost_after_eip_7667()
    {
        var sha256Precompile = Sha256Precompile.Instance;
        var bytes = new byte[1];
        var dataCost = GasCostOf.Sha256PrecompileWordCostEip7667 * EvmPooledMemory.Div32Ceiling((ulong)bytes.Length);

        Assert.That(sha256Precompile.DataGasCost(bytes, Spec), Is.EqualTo(dataCost));
        Assert.That(GasCostOf.Sha256PrecompileWordCostEip7667, Is.EqualTo(60));
    }

    [Test]
    public void Blake2F_precompile_data_cost_before_eip_7667()
    {
        var blake2FPrecompile = Blake2FPrecompile.Instance;
        var spec = SpecProvider.GetSpec((BlockNumber, Timestamp - 1));
        var bytes = new byte[213];
        bytes[3] = 1;

        Assert.That(blake2FPrecompile.DataGasCost(bytes, spec), Is.EqualTo(GasCostOf.Blake2GFRound));
        Assert.That(GasCostOf.Blake2GFRound, Is.EqualTo(1));
    }
    
    [Test]
    public void Blake2F_precompile_data_cost_after_eip_7667()
    {
        var blake2FPrecompile = Blake2FPrecompile.Instance;
        var bytes = new byte[213];
        bytes[3] = 1;

        Assert.That(blake2FPrecompile.DataGasCost(bytes, Spec), Is.EqualTo(GasCostOf.Blake2GFRoundEip7667));
        Assert.That(GasCostOf.Blake2GFRoundEip7667, Is.EqualTo(10));
    }
}
