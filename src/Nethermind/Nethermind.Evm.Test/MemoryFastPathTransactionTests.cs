// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Runs random transactions over three contracts that call each other on the untraced tables, whose MLOAD and MSTORE
/// try a fast path first, and on the traced tables, which have none.
/// </summary>
/// <remarks>
/// Each table runs on a chain of its own with the same history, and every transaction's status, gas, output, logs and
/// storage must agree. <see cref="HostMemoryFastPathTests"/> drives one frame; this reaches memory across frames:
/// pooled frames and arrays reused with stale bytes, call input and output, return data, copies, logs and creation.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class MemoryFastPathTransactionTests
{
    private const byte STOP = (byte)Instruction.STOP, ADD = (byte)Instruction.ADD, SUB = (byte)Instruction.SUB,
        SHL = (byte)Instruction.SHL, KECCAK256 = (byte)Instruction.KECCAK256, CALLDATACOPY = (byte)Instruction.CALLDATACOPY,
        CODECOPY = (byte)Instruction.CODECOPY, RETURNDATASIZE = (byte)Instruction.RETURNDATASIZE,
        RETURNDATACOPY = (byte)Instruction.RETURNDATACOPY, POP = (byte)Instruction.POP, MLOAD = (byte)Instruction.MLOAD,
        MSTORE = (byte)Instruction.MSTORE, MSTORE8 = (byte)Instruction.MSTORE8, SLOAD = (byte)Instruction.SLOAD,
        SSTORE = (byte)Instruction.SSTORE, JUMPI = (byte)Instruction.JUMPI, MSIZE = (byte)Instruction.MSIZE,
        GAS = (byte)Instruction.GAS, JUMPDEST = (byte)Instruction.JUMPDEST, MCOPY = (byte)Instruction.MCOPY,
        PUSH0 = (byte)Instruction.PUSH0, PUSH1 = (byte)Instruction.PUSH1, PUSH2 = (byte)Instruction.PUSH2,
        PUSH3 = (byte)Instruction.PUSH3, PUSH4 = (byte)Instruction.PUSH4, PUSH5 = (byte)Instruction.PUSH5,
        PUSH20 = (byte)Instruction.PUSH20, PUSH32 = (byte)Instruction.PUSH32, DUP1 = (byte)Instruction.DUP1,
        DUP2 = (byte)Instruction.DUP2, SWAP1 = (byte)Instruction.SWAP1, LOG1 = (byte)Instruction.LOG1,
        CREATE = (byte)Instruction.CREATE, CALL = (byte)Instruction.CALL, RETURN = (byte)Instruction.RETURN,
        DELEGATECALL = (byte)Instruction.DELEGATECALL, STATICCALL = (byte)Instruction.STATICCALL, REVERT = (byte)Instruction.REVERT;

    private const int TransactionsPerFork = 300;
    private const int Contracts = 3;
    private const int StorageSlots = 16;

    private static readonly Address Sender = TestItem.AddressA;

    /// <summary>The dispatch tables a transaction runs on; the traced ones are the reference.</summary>
    private enum Table
    {
        Traced,
        NoTrace,
        NoTraceCancelable,
    }

    private static IEnumerable<TestCaseData> Forks()
    {
        (string Name, ForkActivation Activation)[] forks =
        [
            ("Cancun", MainnetSpecProvider.CancunActivation),
            ("Prague", MainnetSpecProvider.PragueActivation),
            ("Osaka", MainnetSpecProvider.OsakaActivation),
            ("Amsterdam", MainnetSpecProvider.AmsterdamActivation),
        ];
        for (int seed = 0; seed < forks.Length; seed++)
            yield return new TestCaseData(forks[seed].Activation, seed).SetName($"{{m}}({forks[seed].Name})");
    }

    [TestCaseSource(nameof(Forks))]
    public void Untraced_tables_match_the_traced_ones_across_frames(ForkActivation activation, int seed)
    {
        IReleaseSpec spec = MainnetSpecProvider.Instance.GetSpec(activation);
        Table[] tables = Enum.GetValues<Table>();
        Chain[] chains = new Chain[tables.Length];
        List<string> mismatches = [];
        int succeeded = 0;
        try
        {
            for (int t = 0; t < chains.Length; t++)
                chains[t] = new Chain(MainnetSpecProvider.Instance);

            for (int i = 0; i < TransactionsPerFork && mismatches.Count < 5; i++)
            {
                Random random = new(seed * 1_000_003 + i);
                byte[] input = new byte[random.Next(0, 200)];
                random.NextBytes(input);
                ulong gasLimit = random.Next(6) switch
                {
                    0 => 60_000UL + (ulong)random.Next(0, 200_000),
                    1 => 21_000UL + (ulong)random.Next(0, 600_000),
                    2 => 1_000_000UL,
                    _ => 3_000_000UL,
                };
                Address[] addresses = new Address[Contracts];
                for (int c = 0; c < Contracts; c++)
                    addresses[c] = Address.FromNumber((UInt256)(0x10_0000_0000UL + (ulong)i * Contracts + (ulong)c));
                byte[][] codes = new byte[Contracts][];
                for (int c = 0; c < Contracts; c++)
                    codes[c] = Instantiate(Generate(random, top: c == 0), addresses);

                string? reference = null;
                for (int t = 0; t < tables.Length; t++)
                {
                    string digest = chains[t].Run(tables[t], activation, spec, addresses, codes, input, gasLimit);
                    if (tables[t] == Table.Traced)
                    {
                        reference = digest;
                        if (digest.StartsWith("ok ", StringComparison.Ordinal)) succeeded++;
                    }
                    else if (digest != reference)
                        mismatches.Add($"tx {i} {tables[t]}\n {digest}\n traced {reference}\n codes {string.Join(" ", Array.ConvertAll(codes, Convert.ToHexString))} input {Convert.ToHexString(input)}");
                }
            }
        }
        finally
        {
            foreach (Chain? chain in chains)
                chain?.Dispose();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mismatches, Is.Empty);
            Assert.That(succeeded, Is.GreaterThan(TransactionsPerFork / 4), "too few transactions succeed for the digests to compare much");
        }
    }

    /// <summary>A world state and a transaction processor over it, resolved from the production modules.</summary>
    private sealed class Chain : IDisposable
    {
        private readonly IContainer _container;
        private readonly ILifetimeScope _processingScope;
        private readonly IWorldState _state;
        private readonly IDisposable _scope;
        private readonly ITransactionProcessor _processor;
        private readonly IReleaseSpec _genesisSpec;

        public Chain(ISpecProvider specProvider)
        {
            _genesisSpec = specProvider.GenesisSpec;
            _container = new ContainerBuilder()
                .AddModule(new TestNethermindModule())
                .AddSingleton<ISpecProvider>(specProvider)
                .Build();
            _processingScope = _container.BeginLifetimeScope(builder => builder
                .AddSingleton<IWorldStateScopeProvider>(_container.Resolve<IWorldStateManager>().GlobalWorldState));
            _state = _processingScope.Resolve<IWorldState>();
            _processor = _processingScope.Resolve<ITransactionProcessor>();
            _scope = _state.BeginScope(IWorldState.PreGenesis);
            _state.CreateAccount(Sender, (UInt256)ulong.MaxValue);
            _state.Commit(_genesisSpec);
            _state.CommitTree(0);
        }

        /// <summary>Deploys the contracts, calls the first of them, and digests what the transaction did.</summary>
        public string Run(Table table, ForkActivation activation, IReleaseSpec spec, Address[] addresses, byte[][] codes, byte[] input, ulong gasLimit)
        {
            for (int c = 0; c < addresses.Length; c++)
            {
                _state.CreateAccount(addresses[c], 0);
                _state.InsertCode(addresses[c], codes[c], _genesisSpec);
            }

            _state.Commit(_genesisSpec);
            _state.CommitTree(0);

            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithGasPrice(1)
                .WithNonce(_state.GetNonce(Sender))
                .WithData(input)
                .WithValue(0)
                .To(addresses[0])
                .WithSenderAddress(Sender)
                .TestObject;
            Block block = Build.A.Block.WithNumber(activation.BlockNumber)
                .WithTimestamp(activation.Timestamp ?? 0)
                .WithTransactions(tx)
                .WithGasLimit(8_000_000)
                .WithBeneficiary(TestItem.AddressD)
                .WithBlobGasUsed(0)
                .WithExcessBlobGas(0)
                .WithParentBeaconBlockRoot(TestItem.KeccakG)
                .TestObject;

            DigestTracer tracer = new(table);
            string exception = "";
            try
            {
                _processor.Execute(tx, new BlockExecutionContext(block.Header, spec), tracer);
            }
            catch (Exception e)
            {
                // A transaction the processor rejects must be rejected on every table alike.
                exception = $" exception {e.GetType().Name}: {e.Message}";
            }

            StringBuilder digest = new(tracer.Result + exception);
            digest.Append(" storage");
            foreach (Address address in addresses)
            {
                for (int slot = 0; slot < StorageSlots; slot++)
                {
                    _state.Get(new StorageCell(address, (UInt256)slot), out UInt256 value);
                    digest.Append(' ').Append(value.ToString());
                }
            }

            return digest.ToString();
        }

        public void Dispose()
        {
            _scope.Dispose();
            _processingScope.Dispose();
            _container.Dispose();
        }
    }

    private sealed class DigestTracer : TxTracer, ITxTracer
    {
        private readonly bool _cancelable;

        public DigestTracer(Table table)
        {
            IsTracingReceipt = true;
            IsTracingInstructions = table == Table.Traced;
            _cancelable = table == Table.NoTraceCancelable;
        }

        bool ITxTracer.IsCancelable => _cancelable;
        public string Result { get; private set; } = "none";

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            StringBuilder result = new($"ok gas {gasSpent.SpentGas} block gas {gasSpent.EffectiveBlockGas} output {Keccak.Compute(output)} logs");
            foreach (LogEntry log in logs)
                result.Append(' ').Append(log.Address).Append(':').Append(string.Join<Hash256>(",", log.Topics)).Append(':').Append(Keccak.Compute(log.Data));
            Result = result.ToString();
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null) =>
            Result = $"failed gas {gasSpent.SpentGas} block gas {gasSpent.EffectiveBlockGas} output {Keccak.Compute(output ?? [])} error {error}";
    }

    /// <summary>A piece of code, or with no bytes the address of contract <see cref="Target"/> pushed by PUSH20.</summary>
    /// <remarks>Targets past the contracts are the identity and SHA-256 precompiles, and an account with no code.</remarks>
    private readonly record struct Piece(byte[]? Bytes, int Target);

    private static byte[] Instantiate(List<Piece> pieces, Address[] addresses)
    {
        List<byte> code = [];
        foreach (Piece piece in pieces)
        {
            if (piece.Bytes is not null)
            {
                code.AddRange(piece.Bytes);
                continue;
            }

            code.Add(PUSH20);
            code.AddRange(piece.Target switch
            {
                < Contracts => addresses[piece.Target].Bytes,
                Contracts => Address.FromNumber(4).Bytes,
                Contracts + 1 => Address.FromNumber(2).Bytes,
                _ => Address.FromNumber(0xdead_beef).Bytes,
            });
        }

        return code.ToArray();
    }

    private sealed class Builder(bool top)
    {
        private readonly List<byte> _current = [];

        public bool Top { get; } = top;
        public List<Piece> Pieces { get; } = [];
        public int Length { get; private set; }

        public void Add(params byte[] bytes)
        {
            _current.AddRange(bytes);
            Length += bytes.Length;
        }

        public void AddTarget(int target)
        {
            Flush();
            Pieces.Add(new Piece(null, target));
            Length += 21;
        }

        public void Flush()
        {
            if (_current.Count == 0) return;
            Pieces.Add(new Piece(_current.ToArray(), 0));
            _current.Clear();
        }
    }

    private static List<Piece> Generate(Random random, bool top)
    {
        Builder b = new(top);
        int snippets = random.Next(3, 45);
        for (int s = 0; s < snippets; s++)
            Snippet(b, random, allowLoop: true);

        // The top frame mostly returns its whole memory, so what its memory holds is part of the digest.
        switch (top ? random.Next(3) : random.Next(10))
        {
            case < 5: b.Add(MSIZE, PUSH1, 0, RETURN); break;
            case < 6: PushLength(b, random); PushOffset(b, random); b.Add(REVERT); break;
            case < 8: PushLength(b, random); PushOffset(b, random); b.Add(RETURN); break;
            case < 9: b.Add(STOP); break;
        }

        b.Flush();
        return b.Pieces;
    }

    private static void Snippet(Builder b, Random random, bool allowLoop)
    {
        switch (random.Next(120))
        {
            case < 16: PushValue(b, random); PushOffset(b, random); b.Add(MSTORE); break;
            case < 24: PushOffset(b, random); b.Add(MLOAD); PushKey(b, random); b.Add(SSTORE); break;
            case < 30: PushOffset(b, random); b.Add(MLOAD, POP); break;
            case < 36: PushOffset(b, random); b.Add(MLOAD); PushOffset(b, random); b.Add(MSTORE); break;
            case < 40: PushValue(b, random); PushOffset(b, random); b.Add(MSTORE8); break;
            case < 45: b.Add(MSIZE, MSIZE, MSTORE); break;
            case < 48: b.Add(MSIZE, PUSH1, (byte)random.Next(0, 70), SWAP1, SUB, MLOAD, POP); break;
            case < 50: b.Add(MSIZE, PUSH1, (byte)random.Next(0, 70), SWAP1, SUB, MLOAD); PushKey(b, random); b.Add(SSTORE); break;
            case < 54: PushLength(b, random); PushOffset(b, random); PushOffset(b, random); b.Add(MCOPY); break;
            case < 57: PushLength(b, random); PushOffset(b, random); PushOffset(b, random); b.Add(CALLDATACOPY); break;
            case < 59: PushLength(b, random); PushOffset(b, random); PushOffset(b, random); b.Add(CODECOPY); break;
            case < 62: b.Add(RETURNDATASIZE, PUSH1, 0); PushOffset(b, random); b.Add(RETURNDATACOPY); break;
            case < 65: PushLength(b, random); PushOffset(b, random); b.Add(KECCAK256); PushOffset(b, random); b.Add(MSTORE); break;
            case < 77: Call(b, random); break;
            case < 80: PushValue(b, random); PushLength(b, random); PushOffset(b, random); b.Add(LOG1); break;
            case < 83: b.Add(GAS); PushKey(b, random); b.Add(SSTORE); break;
            case < 85: b.Add(MSIZE); PushKey(b, random); b.Add(SSTORE); break;
            case < 86:
                {
                    // Init code that stores, loads and returns its memory, placed at a random offset for CREATE to copy.
                    byte[] init = new byte[32];
                    ((byte[])[PUSH1, 0xaa, PUSH1, 0, MSTORE, PUSH1, 0x20, MLOAD, POP, MSIZE, PUSH1, 0, RETURN]).CopyTo(init, 0);
                    byte offset = (byte)random.Next(0, 200);
                    b.Add([PUSH32, .. init, PUSH1, offset, MSTORE, PUSH1, 32, PUSH1, offset, PUSH0, CREATE]);
                    PushKey(b, random); b.Add(SSTORE);
                    break;
                }
            case < 88: b.Add(RETURNDATASIZE); PushKey(b, random); b.Add(SSTORE); break;
            case < 89: b.Add(JUMPDEST); break;
            // A bare opcode that faults on a short stack, where only a callee's fault is survivable.
            case < 90 when !b.Top: b.Add(random.Next(3) switch { 0 => MLOAD, 1 => MSTORE, _ => POP }); break;
            case < 93: PushLength(b, random); PushOffset(b, random); b.Add(PUSH0, CREATE); PushKey(b, random); b.Add(SSTORE); break;
            case < 95: b.Add(PUSH1, (byte)random.Next(0, StorageSlots), SLOAD); PushOffset(b, random); b.Add(MSTORE); break;
            case < 110 when allowLoop: Loop(b, random); break;
            default: PushOffset(b, random); b.Add(MLOAD); PushOffset(b, random); b.Add(MSTORE); break;
        }
    }

    /// <summary>A counted loop over word-indexed memory, the shape of Solidity's array loops.</summary>
    private static void Loop(Builder b, Random random)
    {
        b.Add(PUSH1, (byte)random.Next(1, 70));
        int label = b.Length;
        b.Add(JUMPDEST);
        int body = random.Next(1, 4);
        for (int k = 0; k < body; k++)
        {
            switch (random.Next(8))
            {
                case 0: b.Add(MSIZE, MSIZE, MSTORE); break;
                case 1: b.Add(DUP1, PUSH1, 5, SHL, MLOAD, POP); break;
                case 2: b.Add(GAS, DUP2, PUSH1, 5, SHL, MSTORE); break;
                case 3: b.Add(DUP1, PUSH1, 5, SHL, PUSH2, (byte)random.Next(0, 16), (byte)random.Next(256), ADD, MLOAD); PushKey(b, random); b.Add(SSTORE); break;
                case 4: b.Add(DUP1, DUP2, PUSH1, 5, SHL, MSTORE); break;
                case 5: b.Add(DUP1, PUSH1, (byte)random.Next(0, 64), ADD, MLOAD, POP); break;
                case 6: b.Add(DUP1, PUSH1, (byte)random.Next(0, 64), ADD, DUP2, SWAP1, MSTORE); break;
                default: Snippet(b, random, allowLoop: false); break;
            }
        }

        b.Add(PUSH1, 1, SWAP1, SUB, DUP1, PUSH2, (byte)(label >> 8), (byte)label, JUMPI, POP);
    }

    private static void Call(Builder b, Random random)
    {
        int kind = random.Next(3);
        PushLength(b, random); PushOffset(b, random, small: true);
        PushLength(b, random); PushOffset(b, random, small: true);
        if (kind == 0) b.Add(PUSH0);
        b.AddTarget(random.Next(7) switch { 0 or 1 => 1, 2 => 2, 3 => 0, 4 => Contracts, 5 => Contracts + 1, _ => Contracts + 2 });
        switch (b.Top && random.Next(6) != 0 ? 1 + random.Next(3) : random.Next(4))
        {
            case 0: b.Add(GAS); break;
            case 1: b.Add(PUSH3, 0, (byte)random.Next(0, 0xff), (byte)random.Next(256)); break;
            case 2: b.Add(PUSH2, (byte)random.Next(0, 0x10), (byte)random.Next(256)); break;
            default: b.Add(PUSH3, (byte)random.Next(0, 3), (byte)random.Next(256), (byte)random.Next(256)); break;
        }

        b.Add(kind switch { 0 => CALL, 1 => STATICCALL, _ => DELEGATECALL });
        if (random.Next(2) == 0) { PushKey(b, random); b.Add(SSTORE); }
        else b.Add(POP);
    }

    private static void PushKey(Builder b, Random random) => b.Add(PUSH1, (byte)random.Next(0, StorageSlots));

    /// <summary>Pushes a memory offset on a boundary a fast path tests; the top frame rarely takes one that only runs out of gas.</summary>
    private static void PushOffset(Builder b, Random random, bool small = false)
    {
        int pick = random.Next(small ? 6 : 18);
        if (pick is >= 10 and <= 13 && (b.Top || random.Next(12) != 0)) pick = 8;
        switch (pick)
        {
            case 0: b.Add(PUSH1, (byte)random.Next(0, 70)); break;
            case 1: b.Add(PUSH1, (byte)(random.Next(0, 8) * 32)); break;
            case 2: Push2(b, random.Next(950, 1100)); break;
            case 3: Push2(b, random.Next(224, 290)); break;
            case 4: b.Add(MSIZE); break;
            case 5: b.Add(MSIZE, PUSH1, 32, SWAP1, SUB); break;
            case 6: Push2(b, random.Next(2000, 2100)); break;
            case 7: Push2(b, random.Next(4040, 4140)); break;
            case 8: Push2(b, random.Next(0, 8192)); break;
            case 9: Push2(b, (random.Next(1, 16) * 1024 + random.Next(-40, 40)) & 0xffff); break;
            case 10: b.Add(PUSH4, 0x7f, 0xff, 0xff, (byte)random.Next(0xc0, 0x100)); break;
            case 11: b.Add(PUSH4, 0xff, 0xff, 0xff, (byte)random.Next(0xc0, 0x100)); break;
            case 12: b.Add(PUSH5, 1, 0, 0, 0, (byte)random.Next(0, 64)); break;
            case 13:
                {
                    byte[] word = new byte[32];
                    word[random.Next(0, 24)] = (byte)random.Next(1, 256);
                    word[31] = (byte)random.Next(0, 64);
                    b.Add([PUSH32, .. word]);
                    break;
                }
            case 14: b.Add(PUSH3, 0, (byte)random.Next(0x40, 0x80), (byte)random.Next(256)); break;
            default: b.Add(PUSH1, (byte)random.Next(0, 256)); break;
        }
    }

    private static void Push2(Builder b, int value) => b.Add(PUSH2, (byte)(value >> 8), (byte)value);

    private static void PushValue(Builder b, Random random)
    {
        switch (random.Next(5))
        {
            case 0: b.Add(PUSH1, (byte)random.Next(0, 256)); break;
            case 1: b.Add(PUSH0); break;
            case 2: b.Add(GAS); break;
            default:
                {
                    byte[] word = new byte[32];
                    random.NextBytes(word);
                    b.Add([PUSH32, .. word]);
                    break;
                }
        }
    }

    private static void PushLength(Builder b, Random random)
    {
        switch (random.Next(5))
        {
            case 0: b.Add(PUSH0); break;
            case 1: Push2(b, random.Next(0, 2100)); break;
            case 2: b.Add(PUSH1, 32); break;
            default: b.Add(PUSH1, (byte)random.Next(0, 100)); break;
        }
    }
}
