namespace Nevermind.Evm.Abi
{
    public class AbiSignature
    {
        public AbiSignature(string name, params AbiType[] types)
        {
            Name = name;
            Types = types;
        }

        public string Name { get; }
        public AbiType[] Types { get; }
    }
}