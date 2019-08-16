namespace Nethermind.Abi
{
    public class AbiEncodingInfo
    {
        public AbiEncodingStyle EncodingStyle { get; }
        public AbiSignature Signature { get; }

        public AbiEncodingInfo(AbiEncodingStyle encodingStyle, AbiSignature signature)
        {
            EncodingStyle = encodingStyle;
            Signature = signature;
        }
    }
}