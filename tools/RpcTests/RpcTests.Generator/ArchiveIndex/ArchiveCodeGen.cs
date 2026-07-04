// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.RpcTests.Generator.ArchiveIndex;

/// <summary>
/// Emits EVM runtime bytecode for archive-index probe calls: a driver contract that reads account balances and
/// STATICCALLs per-contract slot readers, and the slot-reader contracts themselves.
/// </summary>
internal static class ArchiveCodeGen
{
    // Driver memory layout. The first four words are RETURNed as the ArchiveProbeReturn struct; the scratch
    // words hold each STATICCALL's return data (the contract's own fingerprint + count) before it is folded in.
    private const byte AccountFprOffset = 0x00;   // XOR of every BALANCE read
    private const byte StorageFprOffset = 0x20;   // XOR of every contract's returned storage fingerprint
    private const byte AccountCountOffset = 0x40; // count of non-zero balances
    private const byte SlotCountOffset = 0x60;    // count of non-zero storage slots
    private const byte RetOffset = 0x80;          // STATICCALL return scratch: contract fingerprint
    private const byte RetCountOffset = 0xA0;     // STATICCALL return scratch: contract non-zero count
    private const byte ReturnSize = 0x80;         // the four result words returned to the caller

    // Slot-reader memory layout (its own execution frame), RETURNed to the driver as two words.
    private const byte SlotFprOffset = 0x00;
    private const byte SlotCounterOffset = 0x20;
    private const byte SlotReturnSize = 0x40;

    // EVM opcodes
    private const byte OpAdd = 0x01;
    private const byte OpIsZero = 0x15;
    private const byte OpXor = 0x18;
    private const byte OpBalance = 0x31;
    private const byte OpPop = 0x50;
    private const byte OpMload = 0x51;
    private const byte OpMstore = 0x52;
    private const byte OpSload = 0x54;
    private const byte OpGas = 0x5A;
    private const byte OpPush1 = 0x60;
    private const byte OpPush20 = 0x73;
    private const byte OpPush32 = 0x7F;
    private const byte OpDup1 = 0x80;
    private const byte OpStaticCall = 0xFA;
    private const byte OpReturn = 0xF3;

    /// <summary>
    /// Driver runtime code: BALANCE every account (folding each into the account fingerprint/counter) and
    /// STATICCALL every contract with slots (its overridden code reads the storage and returns its own
    /// fingerprint/count, which are folded into the storage accumulators). Ends by RETURNing the 4-word struct.
    /// </summary>
    public static string BuildDriverCode(Sweep sweep)
    {
        List<byte> code = new(sweep.Accounts.Count * 40 + sweep.Contracts.Count * 64 + 8);

        foreach (byte[] account in sweep.Accounts)
        {
            code.Add(OpPush20); code.AddRange(account);
            code.Add(OpBalance);
            EmitXorFold(code, AccountFprOffset, AccountCountOffset);
        }

        foreach (Contract contract in sweep.Contracts)
        {
            // Clear the return scratch first: memory persists across STATICCALLs, so a failed call (which copies
            // no return data) would otherwise re-fold the previous contract's values.
            EmitStoreZero(code, RetOffset);
            EmitStoreZero(code, RetCountOffset);

            // STATICCALL(gas, to, argsOffset=0, argsLength=0, retOffset=RetOffset, retLength=SlotReturnSize)
            code.Add(OpPush1); code.Add(SlotReturnSize); // retLength: fingerprint + count
            code.Add(OpPush1); code.Add(RetOffset);      // retOffset
            code.Add(OpPush1); code.Add(0x00);           // argsLength
            code.Add(OpPush1); code.Add(0x00);           // argsOffset
            code.Add(OpPush20); code.AddRange(contract.Address); // to
            code.Add(OpGas);                             // forward all remaining gas
            code.Add(OpStaticCall);
            code.Add(OpPop); // ignore success; a failed call left the scratch zeroed above

            EmitMemFold(code, OpXor, StorageFprOffset, RetOffset);    // storageFpr ^= contract fingerprint
            EmitMemFold(code, OpAdd, SlotCountOffset, RetCountOffset); // slotCount += contract non-zero count
        }

        code.Add(OpPush1); code.Add(ReturnSize); // length
        code.Add(OpPush1); code.Add(0x00);       // offset
        code.Add(OpReturn);
        return ToHex(code);
    }

    /// <summary>Per-contract override code: SLOAD each slot (folding into a local fingerprint/counter), then
    /// RETURN those two words to the driver.</summary>
    public static string BuildSlotReaderCode(IReadOnlyList<byte[]> slots)
    {
        List<byte> code = new(slots.Count * 52 + 8);
        foreach (byte[] slot in slots)
        {
            code.Add(OpPush32); code.AddRange(slot);
            code.Add(OpSload);
            EmitXorFold(code, SlotFprOffset, SlotCounterOffset);
        }

        code.Add(OpPush1); code.Add(SlotReturnSize); // length
        code.Add(OpPush1); code.Add(0x00);           // offset
        code.Add(OpReturn);
        return ToHex(code);
    }

    /// <summary>Consumes the 256-bit value on top of the stack: XORs it into the fingerprint at
    /// <paramref name="fprOffset"/> and, when it is non-zero, increments the counter at <paramref name="cntOffset"/>.</summary>
    private static void EmitXorFold(List<byte> code, byte fprOffset, byte cntOffset)
    {
        // mem[fpr] ^= value
        code.Add(OpDup1);
        code.Add(OpPush1); code.Add(fprOffset); code.Add(OpMload);
        code.Add(OpXor);
        code.Add(OpPush1); code.Add(fprOffset); code.Add(OpMstore);
        // mem[cnt] += (value != 0)
        code.Add(OpIsZero); code.Add(OpIsZero);
        code.Add(OpPush1); code.Add(cntOffset); code.Add(OpMload);
        code.Add(OpAdd);
        code.Add(OpPush1); code.Add(cntOffset); code.Add(OpMstore);
    }

    /// <summary>Combines mem[<paramref name="srcOffset"/>] into mem[<paramref name="dstOffset"/>] with the given
    /// binary op (<see cref="OpXor"/> or <see cref="OpAdd"/>).</summary>
    private static void EmitMemFold(List<byte> code, byte op, byte dstOffset, byte srcOffset)
    {
        code.Add(OpPush1); code.Add(srcOffset); code.Add(OpMload);
        code.Add(OpPush1); code.Add(dstOffset); code.Add(OpMload);
        code.Add(op);
        code.Add(OpPush1); code.Add(dstOffset); code.Add(OpMstore);
    }

    private static void EmitStoreZero(List<byte> code, byte offset)
    {
        code.Add(OpPush1); code.Add(0x00);
        code.Add(OpPush1); code.Add(offset);
        code.Add(OpMstore);
    }

    private static string ToHex(List<byte> code) => "0x" + Convert.ToHexString([.. code]).ToLowerInvariant();
}
