// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Shared bytecode and storage helpers for <c>BlockProcessingBenchmark</c> and its
/// validation tests. Keeps contract definitions in one place so the test validates
/// the exact same bytecode the benchmark executes.
/// </summary>
public static class StorageBenchmarkContracts
{
    // ── Transfer event: keccak256("Transfer(address,address,uint256)") ────
    private static readonly byte[] TransferEventTopic = Convert.FromHexString(
        "ddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef");

    // ── Swap event: arbitrary 32-byte topic (exact value irrelevant for benchmarking) ──
    private static readonly byte[] SwapEventTopic = Convert.FromHexString(
        "c42079f94a6350d7e6235f29174924f928cc2ac818eb64fed8004e115fbcca67");

    /// <summary>
    /// ERC20-style transfer runtime bytecode.
    /// <para>
    /// Calldata layout (no function selector): <c>[to (32 bytes), amount (32 bytes)]</c>.
    /// Storage layout: <c>balances[addr] = keccak256(abi.encode(addr, 0))</c> (Solidity mapping at slot 0).
    /// </para>
    /// <para>Per call: 2 KECCAK256, 2 SLOAD, 2 SSTORE, 1 LOG3 (Transfer event).</para>
    /// </summary>
    public static byte[] BuildErc20RuntimeCode()
    {
        return Prepare.EvmCode
            // Load to and amount from calldata
            .PushData(0)
            .Op(Instruction.CALLDATALOAD)       // [to]
            .PushData(32)
            .Op(Instruction.CALLDATALOAD)       // [amount, to]

            // ── Compute sender balance slot: keccak256(abi.encode(caller, 0)) ──
            .Op(Instruction.CALLER)             // [caller, amount, to]
            .PushData(0)
            .Op(Instruction.MSTORE)             // mem[0..31] = caller
            .PushData(0)
            .PushData(32)
            .Op(Instruction.MSTORE)             // mem[32..63] = 0 (slot number)
            .PushData(64)
            .PushData(0)
            .Op(Instruction.KECCAK256)          // [senderSlot, amount, to]

            // Load sender balance
            .Op(Instruction.DUP1)
            .Op(Instruction.SLOAD)              // [senderBal, senderSlot, amount, to]

            // newSenderBal = senderBal - amount
            .Op(Instruction.DUP3)               // [amount, senderBal, senderSlot, amount, to]
            .Op(Instruction.SWAP1)              // [senderBal, amount, senderSlot, amount, to]
            .Op(Instruction.SUB)                // [newSenderBal, senderSlot, amount, to]

            // SSTORE(senderSlot, newSenderBal)
            .Op(Instruction.SWAP1)              // [senderSlot, newSenderBal, amount, to]
            .Op(Instruction.SSTORE)             // [amount, to]

            // ── Compute recipient balance slot: keccak256(abi.encode(to, 0)) ──
            .Op(Instruction.DUP2)               // [to, amount, to]
            .PushData(0)
            .Op(Instruction.MSTORE)             // mem[0..31] = to
                                                // mem[32..63] still = 0 from above
            .PushData(64)
            .PushData(0)
            .Op(Instruction.KECCAK256)          // [recipientSlot, amount, to]

            // Load recipient balance
            .Op(Instruction.DUP1)
            .Op(Instruction.SLOAD)              // [recipientBal, recipientSlot, amount, to]

            // newRecipientBal = recipientBal + amount
            .Op(Instruction.DUP3)               // [amount, recipientBal, recipientSlot, amount, to]
            .Op(Instruction.ADD)                // [newRecipientBal, recipientSlot, amount, to]

            // SSTORE(recipientSlot, newRecipientBal)
            .Op(Instruction.SWAP1)              // [recipientSlot, newRecipientBal, amount, to]
            .Op(Instruction.SSTORE)             // [amount, to]

            // ── Emit Transfer(from, to, amount) via LOG3 ──
            // Store amount in memory for event data
            .Op(Instruction.DUP1)               // [amount, amount, to]
            .PushData(64)
            .Op(Instruction.MSTORE)             // mem[64..95] = amount; [amount, to]
            .Op(Instruction.POP)                // [to]

            // Push LOG3 args (deepest stack item first):
            //   topic2=to (already on stack), topic1=caller, topic0=TransferSig, size, offset
            .Op(Instruction.CALLER)             // [caller, to]
            .PushData(TransferEventTopic)       // [transferSig, caller, to]
            .PushData(32)                       // [32, transferSig, caller, to]
            .PushData(64)                       // [64, 32, transferSig, caller, to]
            .Op(Instruction.LOG3)               // []

            .Op(Instruction.STOP)
            .Done;
    }

    /// <summary>
    /// DEX swap-style runtime bytecode with heavy storage access.
    /// <para>
    /// Calldata layout: <c>[amountIn (32 bytes)]</c>.
    /// Storage layout: fixed slots 0-7 for pool state, mapping at slot 8 for user balances.
    /// </para>
    /// <para>Per call: 8 SLOAD, 6 SSTORE, 1 KECCAK256, 1 LOG1.</para>
    /// </summary>
    public static byte[] BuildSwapRuntimeCode()
    {
        return Prepare.EvmCode
            // Load amountIn from calldata
            .PushData(0)
            .Op(Instruction.CALLDATALOAD)       // [amountIn]

            // ── Slot 0: reserve0 += amountIn ──
            .PushData(0)
            .Op(Instruction.SLOAD)              // [reserve0, amountIn]
            .Op(Instruction.DUP2)               // [amountIn, reserve0, amountIn]
            .Op(Instruction.ADD)                // [newReserve0, amountIn]
            .PushData(0)
            .Op(Instruction.SSTORE)             // [amountIn]

            // ── Slot 1: reserve1 -= amountIn / 2 ──
            .Op(Instruction.DUP1)               // [amountIn, amountIn]
            .PushData(2)
            .Op(Instruction.SWAP1)              // [amountIn, 2, amountIn]
            .Op(Instruction.DIV)                // [amountOut, amountIn]
            .PushData(1)
            .Op(Instruction.SLOAD)              // [reserve1, amountOut, amountIn]
            .Op(Instruction.SUB)                // [newReserve1, amountIn]  (reserve1 - amountOut)
            .PushData(1)
            .Op(Instruction.SSTORE)             // [amountIn]

            // ── Slot 2: totalLiquidity (read-only) ──
            .PushData(2)
            .Op(Instruction.SLOAD)              // [totalLiquidity, amountIn]
            .Op(Instruction.POP)                // [amountIn]

            // ── Slot 3: feeAccumulator += amountIn * 3 / 1000 ──
            .Op(Instruction.DUP1)               // [amountIn, amountIn]
            .PushData(3)
            .Op(Instruction.MUL)                // [amountIn*3, amountIn]
            .PushData(1000)
            .Op(Instruction.SWAP1)              // [amountIn*3, 1000, amountIn]
            .Op(Instruction.DIV)                // [fee, amountIn]
            .PushData(3)
            .Op(Instruction.SLOAD)              // [feeAcc, fee, amountIn]
            .Op(Instruction.ADD)                // [newFeeAcc, amountIn]
            .PushData(3)
            .Op(Instruction.SSTORE)             // [amountIn]

            // ── Slot 4: lastTimestamp = block.timestamp ──
            .Op(Instruction.TIMESTAMP)          // [timestamp, amountIn]
            .PushData(4)
            .Op(Instruction.SSTORE)             // [amountIn]

            // ── Slot 5: priceCumulative0 += 1 ──
            .PushData(5)
            .Op(Instruction.SLOAD)              // [priceCum0, amountIn]
            .PushData(1)
            .Op(Instruction.ADD)                // [newPriceCum0, amountIn]
            .PushData(5)
            .Op(Instruction.SSTORE)             // [amountIn]

            // ── Slot 6: priceCumulative1 (read-only) ──
            .PushData(6)
            .Op(Instruction.SLOAD)              // [priceCum1, amountIn]
            .Op(Instruction.POP)                // [amountIn]

            // ── Slot 7: kLast (read-only) ──
            .PushData(7)
            .Op(Instruction.SLOAD)              // [kLast, amountIn]
            .Op(Instruction.POP)                // [amountIn]

            // ── Mapping at slot 8: senderBalance += amountIn / 2 ──
            .Op(Instruction.CALLER)             // [caller, amountIn]
            .PushData(0)
            .Op(Instruction.MSTORE)             // mem[0..31] = caller
            .PushData(8)
            .PushData(32)
            .Op(Instruction.MSTORE)             // mem[32..63] = 8 (mapping slot)
            .PushData(64)
            .PushData(0)
            .Op(Instruction.KECCAK256)          // [senderSlot, amountIn]

            // Load sender balance
            .Op(Instruction.DUP1)
            .Op(Instruction.SLOAD)              // [senderBal, senderSlot, amountIn]

            // senderBal += amountIn / 2
            .Op(Instruction.DUP3)               // [amountIn, senderBal, senderSlot, amountIn]
            .PushData(2)
            .Op(Instruction.SWAP1)              // [amountIn, 2, senderBal, senderSlot, amountIn]
            .Op(Instruction.DIV)                // [amountIn/2, senderBal, senderSlot, amountIn]
            .Op(Instruction.ADD)                // [newSenderBal, senderSlot, amountIn]

            // SSTORE(senderSlot, newSenderBal)
            .Op(Instruction.SWAP1)              // [senderSlot, newSenderBal, amountIn]
            .Op(Instruction.SSTORE)             // [amountIn]

            // ── LOG1 (Swap event) ──
            .PushData(0)
            .Op(Instruction.MSTORE)             // mem[0..31] = amountIn; []
            .PushData(SwapEventTopic)           // [swapSig]
            .PushData(32)                       // [32, swapSig]
            .PushData(0)                        // [0, 32, swapSig]
            .Op(Instruction.LOG1)               // []

            .Op(Instruction.STOP)
            .Done;
    }

    /// <summary>
    /// Computes the storage slot for a Solidity-style mapping: <c>keccak256(abi.encode(key, mappingSlot))</c>.
    /// </summary>
    public static UInt256 ComputeMappingSlot(Address key, UInt256 mappingSlot)
    {
        Span<byte> input = stackalloc byte[64];
        input.Clear();
        key.Bytes.CopyTo(input.Slice(12)); // 20-byte address right-aligned in first 32 bytes
        mappingSlot.ToBigEndian(input.Slice(32));
        ValueHash256 hash = ValueKeccak.Compute(input);
        return new UInt256(hash.Bytes, isBigEndian: true);
    }
}
