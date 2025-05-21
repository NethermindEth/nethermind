// in indexer indexes codehash-codepos -> opcode
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

public sealed class OpcodeIndexer()
{
    private readonly Dictionary<string, Instruction> _index = new();

    public void Index(ReadOnlySpan<byte> code, ValueHash256 codehash)
    {
        Index(_index, code, codehash);
    }

    // adapted form il-evm
    public static void Index(Dictionary<string, Instruction> index, ReadOnlySpan<byte> machineCode, ValueHash256 codehash)
    {
        var slice = 0..machineCode.Length;
        OpcodeMetadata metadata = default;
        for (int i = slice.Start.Value; i < slice.End.Value; i += 1 + metadata.AdditionalBytes)
        {
            Instruction opcode = (Instruction)machineCode[i];
            metadata = OpcodeMetadata.GetMetadata(opcode);
            index[Key(codehash, (uint)i)] = opcode;

        }
    }

    private static string Key(ValueHash256 codehash, uint codepos)
    {
        return $"{codehash}-{codepos}";
    }

    public Instruction? Get(ValueHash256 codehash, uint codepos)
    {
        return CollectionsMarshal.GetValueRefOrNullRef(_index, Key(codehash, codepos));
    }

}
