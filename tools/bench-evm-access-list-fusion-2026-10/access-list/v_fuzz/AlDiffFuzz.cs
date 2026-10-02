// Verifier-only differential fuzz (not for the PR). Generates random contracts that touch accounts and slots,
// call each other with tight gas, revert, run out of gas, create, self-destruct, and loop over many addresses and
// slots, then records gas, status, state root and the traced access list per transaction. Run the same file at the
// base and at the branch and diff the outputs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
[NonParallelizable]
public class AlDiffFuzz : VirtualMachineTestsBase
{
    private sealed class QuietTracer : TestAllTracerWithOutput
    {
        private readonly bool _actions;
        public QuietTracer(bool access, bool actions = false)
        {
            IsTracingAccess = access;
            _actions = actions;
        }

        public StringBuilder Frames { get; } = new();
        public override bool IsTracingActions => _actions;
        public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
            => Frames.Append('(').Append(gas).Append(' ').Append(to.ToString()[^4..]).Append(' ');
        public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output) => Frames.Append("ok ").Append(gas).Append(')');
        public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) => Frames.Append("created ").Append(gas).Append(')');
        public override void ReportActionError(EvmExceptionType evmExceptionType) => Frames.Append(evmExceptionType).Append(')');
        public override void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output) => Frames.Append("revert ").Append(gas).Append(')');
        public override void ReportActionRemainingGas(ulong gas) => Frames.Append("rem ").Append(gas).Append(' ');
        public override bool IsTracingOpLevelStorage => false;
        public override bool IsTracingMemory => false;
        public override bool IsTracingInstructions => false;
        public override bool IsTracingRefunds => false;
        public override bool IsTracingCode => false;
        public override bool IsTracingStack => false;
        public override bool IsTracingState => false;
        public override bool IsTracingStorage => false;
        public override bool IsTracingBlockHash => false;
        public override bool IsTracingFees => false;
        public List<StorageCell> Cells { get; } = [];

        public override void ReportAccess(IEnumerable<Address> accessedAddresses, IEnumerable<StorageCell> accessedStorageCells)
        {
            base.ReportAccess(accessedAddresses, accessedStorageCells);
            Cells.AddRange(accessedStorageCells);
        }
    }

    private static Address A(string hex40) => new("0x" + hex40);
    private static readonly Address[] Contracts = Enumerable.Range(0, 6).Select(static i => A($"00000000000000000000000000000000c0de{i:x4}")).ToArray();
    private static readonly Address[] Fresh = Enumerable.Range(0, 4).Select(static i => A($"00000000000000000000000000000000dead{i:x4}")).ToArray();
    private static readonly Address[] Precompiles = Enumerable.Range(1, 18).Select(static i => A(i.ToString("x40"))).ToArray();
    private static readonly Address Delegated = A("0000000000000000000000000000000077020000");
    private static readonly UInt256[] AddressLoopBases = [UInt256.Parse("0xbeef00000000"), UInt256.Parse("0xbeef00000400")];

    private static readonly int SeedOffset = int.TryParse(Environment.GetEnvironmentVariable("ALFUZZ_SEED"), out int o) ? o : 0;
    private ForkActivation _activation;
    private bool _delegation;
    protected override ForkActivation Activation => _activation;

    private sealed class Asm
    {
        public readonly List<byte> B = [];
        public int Pc => B.Count;
        public Asm Op(Instruction i) { B.Add((byte)i); return this; }

        public Asm Push(in UInt256 v)
        {
            byte[] be = v.ToBigEndian();
            int skip = 0;
            while (skip < 31 && be[skip] == 0) skip++;
            int len = 32 - skip;
            B.Add((byte)(0x5f + len));
            for (int i = skip; i < 32; i++) B.Add(be[i]);
            return this;
        }

        public Asm Push(int v) => Push((UInt256)(ulong)v);

        public Asm Push(Address a)
        {
            B.Add(0x73);
            B.AddRange(a.Bytes.ToArray());
            return this;
        }

        public Asm Push2(int v)
        {
            B.Add(0x61);
            B.Add((byte)(v >> 8));
            B.Add((byte)v);
            return this;
        }
    }

    private Address PickAddress(Random r)
    {
        int roll = r.Next(100);
        if (roll < 40) return roll < 34 ? Contracts[r.Next(Contracts.Length)] : Recipient;
        if (roll < 58) return Precompiles[r.Next(Precompiles.Length)];
        if (roll < 70) return Fresh[r.Next(Fresh.Length)];
        if (roll < 76) return _delegation ? Delegated : Fresh[0];
        if (roll < 82) return Sender;
        if (roll < 88) return Miner;
        if (roll < 94) return Contract;
        return A(r.Next(0x10000).ToString("x40"));
    }

    private Address PickCallTarget(Random r)
    {
        int roll = r.Next(100);
        if (roll < 60) return roll < 52 ? Contracts[r.Next(Contracts.Length)] : Recipient;
        return PickAddress(r);
    }

    private void EmitAccountAccess(Asm a, Random r)
    {
        Address x = PickAddress(r);
        switch (r.Next(4))
        {
            case 0: a.Push(x).Op(Instruction.BALANCE).Op(Instruction.POP); break;
            case 1: a.Push(x).Op(Instruction.EXTCODESIZE).Op(Instruction.POP); break;
            case 2: a.Push(x).Op(Instruction.EXTCODEHASH).Op(Instruction.POP); break;
            default: a.Push(r.Next(3)).Push(0).Push(0).Push(x).Op(Instruction.EXTCODECOPY); break;
        }
    }

    private static readonly int[] GasChoices = [0, 50, 100, 700, 2599, 2600, 2700, 5000, 30_000, 200_000, -1];

    private void EmitCall(Asm a, Random r)
    {
        Address target = PickCallTarget(r);
        int gas = GasChoices[r.Next(GasChoices.Length)];
        int kind = r.Next(4);
        a.Push(0).Push(0).Push(0).Push(0);
        if (kind is 0 or 3) a.Push(r.Next(100) < 15 ? 1 : 0);
        a.Push(target);
        if (gas < 0) a.Op(Instruction.GAS); else a.Push(gas);
        a.Op(kind switch { 0 => Instruction.CALL, 1 => Instruction.STATICCALL, 2 => Instruction.DELEGATECALL, _ => Instruction.CALLCODE });
        a.Op(Instruction.POP);
    }

    private static void EmitLoop(Asm a, int n, Action<Asm> body)
    {
        a.Push(n);
        int loop = a.Pc;
        a.Op(Instruction.JUMPDEST);
        body(a);
        a.Push(1).Op(Instruction.SWAP1).Op(Instruction.SUB).Op(Instruction.DUP1).Push2(loop).Op(Instruction.JUMPI).Op(Instruction.POP);
    }

    private void EmitCreate(Asm a, Random r)
    {
        // Init code: touch an account, then revert or stop. 28 bytes, padded right into one word.
        List<byte> init = [0x73, .. PickAddress(r).Bytes.ToArray(), (byte)Instruction.BALANCE, (byte)Instruction.POP];
        if (r.Next(2) == 0) init.AddRange([0x60, 0x00, 0x60, 0x00, (byte)Instruction.REVERT]);
        else init.Add((byte)Instruction.STOP);
        byte[] word = new byte[32];
        init.CopyTo(word);
        a.B.Add(0x7f);
        a.B.AddRange(word);
        a.Push(0).Op(Instruction.MSTORE);
        bool create2 = r.Next(2) == 0 && _activation.BlockNumber >= MainnetSpecProvider.ConstantinopleFixBlockNumber;
        if (create2) a.Push(r.Next(4));
        a.Push(init.Count).Push(0).Push(r.Next(100) < 20 ? 1 : 0);
        a.Op(create2 ? Instruction.CREATE2 : Instruction.CREATE).Op(Instruction.POP);
    }

    private byte[] GenerateCode(Random r, int maxOps, bool isMain = false)
    {
        Asm a = new();
        int ops = r.Next(1, maxOps);
        for (int i = 0; i < ops; i++)
        {
            int roll = r.Next(100);
            if (roll < 24) EmitAccountAccess(a, r);
            else if (roll < 42) a.Push(r.Next(8)).Op(Instruction.SLOAD).Op(Instruction.POP);
            else if (roll < 50) a.Push(r.Next(4)).Push(r.Next(8)).Op(Instruction.SSTORE);
            else if (roll < 76) EmitCall(a, r);
            else if (roll < 81)
            {
                int n = r.Next(1, 1500);
                int slotBase = r.Next(2) * 100;
                EmitLoop(a, n, b => b.Op(Instruction.DUP1).Push(slotBase).Op(Instruction.ADD).Op(Instruction.SLOAD).Op(Instruction.POP));
            }
            else if (roll < 86)
            {
                int n = r.Next(1, 1200);
                UInt256 addressBase = AddressLoopBases[r.Next(2)];
                EmitLoop(a, n, b => b.Op(Instruction.DUP1).Push(addressBase).Op(Instruction.ADD).Op(Instruction.BALANCE).Op(Instruction.POP));
            }
            else if (roll < 90) EmitCreate(a, r);
            else if (isMain && roll < 96) { }
            else if (roll < 92) { a.Push(PickAddress(r)).Op(Instruction.SELFDESTRUCT); return a.B.ToArray(); }
            else if (roll < 95) { a.Push(0).Push(0).Op(Instruction.REVERT); return a.B.ToArray(); }
            else if (roll < 96) { a.Op(Instruction.INVALID); return a.B.ToArray(); }
        }

        int end = isMain ? r.Next(100) % 90 : r.Next(100);
        if (isMain && end < 15) a.Push(0).Push(0).Op(Instruction.REVERT);
        else if (isMain) a.Op(Instruction.STOP);
        else if (end < 55) a.Op(Instruction.STOP);
        else if (end < 80) a.Push(0).Push(0).Op(Instruction.REVERT);
        else if (end < 90) a.Op(Instruction.INVALID);
        else a.Push(0).Push(0).Op(Instruction.RETURN);
        return a.B.ToArray();
    }

    private AccessList? GenerateAccessList(Random r)
    {
        if (r.Next(2) == 0) return null;
        AccessList.Builder builder = new();
        int addresses = r.Next(0, 5);
        for (int i = 0; i < addresses; i++)
        {
            builder.AddAddress(i > 0 && r.Next(6) == 0 ? Contracts[0] : PickAddress(r));
            int keys = r.Next(0, 4);
            for (int k = 0; k < keys; k++) builder.AddStorage((UInt256)(ulong)r.Next(8));
        }

        return builder.Build();
    }

    [TestCase("berlin")]
    [TestCase("london")]
    [TestCase("cancun")]
    [TestCase("prague")]
    [TestCase("osaka")]
    [TestCase("amsterdam")]
    public void Run(string fork)
    {
        _activation = fork switch
        {
            "berlin" => (MainnetSpecProvider.BerlinBlockNumber, 0UL),
            "london" => (MainnetSpecProvider.LondonBlockNumber, 0UL),
            "cancun" => MainnetSpecProvider.CancunActivation,
            "prague" => MainnetSpecProvider.PragueActivation,
            "osaka" => MainnetSpecProvider.OsakaActivation,
            _ => MainnetSpecProvider.AmsterdamActivation,
        };
        IReleaseSpec spec = SpecProvider.GetSpec(_activation);
        _delegation = spec.IsEip7702Enabled;
        int count = int.TryParse(Environment.GetEnvironmentVariable("ALFUZZ_N"), out int n) ? n : 1500;
        string outDir = Environment.GetEnvironmentVariable("ALFUZZ_OUT") ?? "/tmp";
        EthereumEcdsa ecdsa = new(SpecProvider.ChainId);
        StringBuilder log = new();
        int failures = 0, reverts = 0, successes = 0, exceptions = 0;

        for (int seed = 0; seed < count; seed++)
        {
            Random r = new(seed * 7919 + fork.Length + SeedOffset);
            try
            {
                foreach (Address c in Contracts)
                {
                    if (!TestState.AccountExists(c)) TestState.CreateAccount(c, 1.Ether);
                    TestState.InsertCode(c, GenerateCode(r, 14), spec);
                }

                if (_delegation)
                {
                    if (!TestState.AccountExists(Delegated)) TestState.CreateAccount(Delegated, 1.Ether);
                    byte[] designator = [0xef, 0x01, 0x00, .. Contracts[r.Next(Contracts.Length)].Bytes.ToArray()];
                    TestState.InsertCode(Delegated, designator, spec);
                }

                byte[] main = GenerateCode(r, 24, isMain: true);
                ulong gasLimit = r.Next(6) switch { 0 => 120_000UL, 1 or 2 => 1_000_000UL, _ => 5_000_000UL };
                AccessList? accessList = GenerateAccessList(r);
                if (!TestState.AccountExists(Sender)) TestState.CreateAccount(Sender, 100.Ether);
                TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
                    .WithGasLimit(gasLimit)
                    .WithGasPrice(1)
                    .WithValue(r.Next(4) == 0 ? 1 : 0)
                    .WithChainId(SpecProvider.ChainId)
                    .WithNonce(TestState.GetNonce(Sender))
                    .To(Recipient);
                if (accessList is not null) txBuilder = txBuilder.WithType(TxType.AccessList).WithAccessList(accessList);
                Transaction tx = txBuilder.SignedAndResolved(ecdsa, SenderKey).TestObject;

                (Block block, _) = PrepareTx(_activation, gasLimit, main, transaction: tx);
                BlockExecutionContext context = new(block.Header, SpecProvider.GetSpec(block.Header));

                QuietTracer traced = new(access: true);
                TransactionResult tracedResult = _processor.CallAndRestore(tx, in context, traced);

                QuietTracer frames = new(access: false, actions: true);
                _processor.CallAndRestore(tx, in context, frames);

                QuietTracer quiet = new(access: false);
                TransactionResult result = _processor.Execute(tx, in context, quiet);
                TestState.Commit(spec);
                TestState.CommitTree(0);

                if (quiet.StatusCode == StatusCode.Success) successes++; else failures++;
                if (quiet.ReportedActionErrors.Count > 0) reverts++;
                log.Append(seed).Append(' ').Append(result.Error).Append('/').Append(result.EvmExceptionType).Append(' ').Append(quiet.StatusCode).Append(' ').Append(quiet.GasSpent)
                    .Append(' ').Append(TestState.StateRoot).Append(" | traced ").Append(tracedResult.Error).Append('/').Append(tracedResult.EvmExceptionType).Append(' ').Append(traced.StatusCode)
                    .Append(' ').Append(traced.GasSpent).Append(" A[").Append(string.Join(",", traced.AccessedAddresses.Select(static x => x.ToString()[^8..])))
                    .Append("] S[").Append(string.Join(",", traced.Cells.Select(static c => c.Address.ToString()[^4..] + ":" + c.Index))).Append("] F ").Append(frames.GasSpent).Append(' ').Append(frames.Frames).AppendLine();
            }
            catch (Exception e)
            {
                exceptions++;
                log.Append(seed).Append(" EXC ").Append(e.GetType().Name).Append(' ').Append(e.Message.Split('\n')[0]).AppendLine();
            }
        }

        log.Append($"SUMMARY {fork} n={count} success={successes} failed={failures} exceptions={exceptions}").AppendLine();
        File.WriteAllText(Path.Combine(outDir, $"alfuzz-{fork}.txt"), log.ToString());
        TestContext.Out.WriteLine($"SUMMARY {fork} n={count} success={successes} failed={failures} exceptions={exceptions}");
    }
}
