namespace Nevermind.Evm.Abi
{
    public abstract class AbiType
    {
        public static AbiDynamicBytes Bytes { get; } = AbiDynamicBytes.Instance;

        public static AbiAddress Address { get; } = AbiAddress.Instance;

        public static AbiFunction Function { get; } = AbiFunction.Instance;

        public static AbiBool Bool { get; } = AbiBool.Instance;

        public static AbiInt Int { get; } = new AbiInt(256);

        public static AbiUInt UInt { get; } = new AbiUInt(256);

        public static AbiString String { get; } = AbiString.Instance;

        public static AbiFixed Fixed { get; } = new AbiFixed(128, 19);

        public static AbiUFixed UFixed { get; } = new AbiUFixed(128, 19);

        public virtual bool IsDynamic => false;

        public abstract string Name { get; }

        public virtual bool Validate(byte[] data)
        {
            return true;
        }

        public abstract (object, int) Decode(byte[] data, int position);

        public abstract byte[] Encode(object arg);

        public override string ToString()
        {
            return Name;
        }

        public override int GetHashCode()
        {
            return Name.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            AbiType type = obj as AbiType;
            return type != null &&
                   Name == type.Name;
        }

        protected string AbiEncodingExceptionMessage => $"Argument cannot be encoded by { GetType().Name}";
    }
}