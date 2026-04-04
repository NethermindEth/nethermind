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
    /// Realistic DEX swap runtime bytecode with nested CALLs to the ERC20 contract.
    /// Simulates a Uniswap-style swap: transfers token in via CALL to ERC20, updates
    /// reserves and oracle state, transfers token out via another CALL to ERC20.
    /// <para>
    /// Calldata layout: <c>[amountIn (32 bytes)]</c>.
    /// Storage layout: slot 0=reserve0, slot 1=reserve1, slot 2=totalLiquidity,
    ///   slot 3=feeAccumulator, slot 4=lastTimestamp, slot 5/6=priceCumulatives, slot 7=kLast.
    /// </para>
    /// <para>
    /// Per call: 2 nested CALLs to ERC20 (each: 2 SLOAD + 2 SSTORE + 2 KECCAK + LOG3),
    /// plus 8 SLOAD + 6 SSTORE + 1 KECCAK + 1 LOG1 in swap contract itself.
    /// Total: ~12 SLOAD + 10 SSTORE + 5 KECCAK + 2 LOG3 + 1 LOG1 + 2 CALL.
    /// </para>
    /// </summary>
    /// <param name="erc20Address">Address of the deployed ERC20 contract (0x1000).</param>
    public static byte[] BuildSwapRuntimeCode(Address erc20Address)
    {
        // ERC20 transfer calldata: [to (32 bytes), amount (32 bytes)]
        // We build it in memory at offset 128 to avoid clobbering keccak scratch space (0-63).

        return Prepare.EvmCode
            // Load amountIn from calldata
            .PushData(0)
            .Op(Instruction.CALLDATALOAD)       // [amountIn]

            // ── CALL 1: ERC20.transfer(address(this), amountIn) — "transfer in" ──
            // Build calldata at mem[128..191]: [address(this), amountIn]
            .Op(Instruction.DUP1)               // [amountIn, amountIn]
            .Op(Instruction.ADDRESS)            // [self, amountIn, amountIn]
            .PushData(128)
            .Op(Instruction.MSTORE)             // mem[128..159] = self; [amountIn, amountIn]
            .Op(Instruction.DUP1)               // [amountIn, amountIn, amountIn]
            .PushData(160)
            .Op(Instruction.MSTORE)             // mem[160..191] = amountIn; [amountIn, amountIn]
            .Op(Instruction.POP)                // [amountIn]
                                                // CALL(gas, to, value, argsOffset, argsLength, retOffset, retLength)
            .PushData(0)                        // retLength
            .PushData(0)                        // retOffset
            .PushData(64)                       // argsLength (2 * 32 bytes)
            .PushData(128)                      // argsOffset
            .PushData(0)                        // value
            .PushData(erc20Address.Bytes)       // to = ERC20 address
            .PushData(50_000)                   // gas for subcall
            .Op(Instruction.CALL)               // [success, amountIn]
            .Op(Instruction.POP)                // [amountIn] (ignore success for benchmark)

            // ── Slot 0: reserve0 += amountIn ──
            .PushData(0)
            .Op(Instruction.SLOAD)              // [reserve0, amountIn]
            .Op(Instruction.DUP2)               // [amountIn, reserve0, amountIn]
            .Op(Instruction.ADD)                // [newReserve0, amountIn]
            .PushData(0)
            .Op(Instruction.SSTORE)             // [amountIn]

            // ── Calculate amountOut = amountIn / 2 ──
            .Op(Instruction.DUP1)               // [amountIn, amountIn]
            .PushData(2)
            .Op(Instruction.SWAP1)              // [amountIn, 2, amountIn]
            .Op(Instruction.DIV)                // [amountOut, amountIn]

            // ── Slot 1: reserve1 -= amountOut ──
            .Op(Instruction.DUP1)               // [amountOut, amountOut, amountIn]
            .PushData(1)
            .Op(Instruction.SLOAD)              // [reserve1, amountOut, amountOut, amountIn]
            .Op(Instruction.SUB)                // [newReserve1, amountOut, amountIn]
            .PushData(1)
            .Op(Instruction.SSTORE)             // [amountOut, amountIn]

            // ── CALL 2: ERC20.transfer(caller, amountOut) — "transfer out" ──
            // Build calldata at mem[128..191]: [caller, amountOut]
            .Op(Instruction.CALLER)             // [caller, amountOut, amountIn]
            .PushData(128)
            .Op(Instruction.MSTORE)             // mem[128..159] = caller; [amountOut, amountIn]
            .Op(Instruction.DUP1)               // [amountOut, amountOut, amountIn]
            .PushData(160)
            .Op(Instruction.MSTORE)             // mem[160..191] = amountOut; [amountOut, amountIn]
            .Op(Instruction.POP)                // [amountIn]
            .PushData(0)                        // retLength
            .PushData(0)                        // retOffset
            .PushData(64)                       // argsLength
            .PushData(128)                      // argsOffset
            .PushData(0)                        // value
            .PushData(erc20Address.Bytes)       // to = ERC20 address
            .PushData(50_000)                   // gas for subcall
            .Op(Instruction.CALL)               // [success, amountIn]
            .Op(Instruction.POP)                // [amountIn]

            // ── Slot 2: totalLiquidity (read-only) ──
            .PushData(2)
            .Op(Instruction.SLOAD)
            .Op(Instruction.POP)

            // ── Slot 3: feeAccumulator += amountIn * 3 / 1000 ──
            .Op(Instruction.DUP1)
            .PushData(3)
            .Op(Instruction.MUL)
            .PushData(1000)
            .Op(Instruction.SWAP1)
            .Op(Instruction.DIV)
            .PushData(3)
            .Op(Instruction.SLOAD)
            .Op(Instruction.ADD)
            .PushData(3)
            .Op(Instruction.SSTORE)             // [amountIn]

            // ── Slot 4: lastTimestamp = block.timestamp ──
            .Op(Instruction.TIMESTAMP)
            .PushData(4)
            .Op(Instruction.SSTORE)             // [amountIn]

            // ── Slot 5: priceCumulative0 += 1 ──
            .PushData(5)
            .Op(Instruction.SLOAD)
            .PushData(1)
            .Op(Instruction.ADD)
            .PushData(5)
            .Op(Instruction.SSTORE)             // [amountIn]

            // ── Slot 6/7: read-only ──
            .PushData(6).Op(Instruction.SLOAD).Op(Instruction.POP)
            .PushData(7).Op(Instruction.SLOAD).Op(Instruction.POP)

            // ── Mapping at slot 8: senderBalance += amountIn / 2 ──
            .Op(Instruction.CALLER)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(8)
            .PushData(32)
            .Op(Instruction.MSTORE)
            .PushData(64)
            .PushData(0)
            .Op(Instruction.KECCAK256)          // [senderSlot, amountIn]
            .Op(Instruction.DUP1)
            .Op(Instruction.SLOAD)              // [senderBal, senderSlot, amountIn]
            .Op(Instruction.DUP3)
            .PushData(2)
            .Op(Instruction.SWAP1)
            .Op(Instruction.DIV)
            .Op(Instruction.ADD)
            .Op(Instruction.SWAP1)
            .Op(Instruction.SSTORE)             // [amountIn]

            // ── LOG1 (Swap event) ──
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(SwapEventTopic)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.LOG1)

            .Op(Instruction.STOP)
            .Done;
    }

    /// <summary>
    /// Storage-write-heavy contract: writes calldata[0] to 8 sequential storage slots.
    /// <para>Per call: 8 SSTORE, 0 SLOAD.</para>
    /// </summary>
    public static byte[] BuildStorageWriteHeavyCode()
    {
        Prepare code = Prepare.EvmCode
            .PushData(0)
            .Op(Instruction.CALLDATALOAD);      // [value]

        for (int slot = 0; slot < 8; slot++)
        {
            code = code
                .Op(Instruction.DUP1)           // [value, value]
                .PushData(slot)                 // [slot, value, value]
                .Op(Instruction.SSTORE);        // [value]
        }

        return code.Op(Instruction.STOP).Done;
    }

    /// <summary>
    /// Storage-read-heavy contract: reads 8 sequential storage slots and sums them to memory.
    /// <para>Per call: 0 SSTORE, 8 SLOAD.</para>
    /// </summary>
    public static byte[] BuildStorageReadHeavyCode()
    {
        Prepare code = Prepare.EvmCode
            .PushData(0);                       // [accumulator=0]

        for (int slot = 0; slot < 8; slot++)
        {
            code = code
                .PushData(slot)                 // [slot, acc]
                .Op(Instruction.SLOAD)          // [val, acc]
                .Op(Instruction.ADD);           // [acc+val]
        }

        // Store result in memory and return
        return code
            .PushData(0)
            .Op(Instruction.MSTORE)             // mem[0..31] = sum
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;
    }

    /// <summary>
    /// Storage-mixed contract: reads 4 slots, writes 4 slots (value = read + calldata[0]).
    /// <para>Per call: 4 SSTORE, 4 SLOAD.</para>
    /// </summary>
    public static byte[] BuildStorageMixedCode()
    {
        Prepare code = Prepare.EvmCode
            .PushData(0)
            .Op(Instruction.CALLDATALOAD);      // [delta]

        // Read slot 0-3, write to slot 4-7 (read + delta)
        for (int i = 0; i < 4; i++)
        {
            code = code
                .Op(Instruction.DUP1)           // [delta, delta]
                .PushData(i)                    // [readSlot, delta, delta]
                .Op(Instruction.SLOAD)          // [readVal, delta, delta]
                .Op(Instruction.ADD)            // [readVal+delta, delta]
                .PushData(i + 4)                // [writeSlot, readVal+delta, delta]
                .Op(Instruction.SSTORE);        // [delta]
        }

        return code.Op(Instruction.STOP).Done;
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
