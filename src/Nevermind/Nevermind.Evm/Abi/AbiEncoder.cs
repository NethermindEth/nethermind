using System;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm.Abi
{
    public class AbiEncoder
    {
        public byte[] Encode(AbiSignature signature, params byte[][] arguments)
        {
            byte[][] encodedParts = new byte[1 + arguments.Length][];
            for (int i = 0; i < arguments.Length; i++)
            {
                encodedParts[1 + i] = arguments[i];
            }

            encodedParts[0] = ComputeAddress(signature);
            return Bytes.Concat(encodedParts);
        }

        private static byte[] ComputeAddress(AbiSignature signature)
        {
            string[] argTypeNames = new string[signature.Types.Length];
            for (int i = 0; i < signature.Types.Length; i++)
            {
                argTypeNames[i] = signature.Types[i].ToString();
            }

            string typeList = string.Join(",", argTypeNames);
            string signatureString = $"{signature.Name}({typeList})";
            Keccak signatureKeccak = Keccak.Compute(signatureString);
            return signatureKeccak.Bytes.Slice(0, 4);
        }

        private static string ComputeSignature(string functionName, AbiType[] abiTypes)
        {
            string[] argTypeNames = new string[abiTypes.Length];
            for (int i = 0; i < abiTypes.Length; i++)
            {
                argTypeNames[i] = abiTypes[i].ToString();
            }

            string typeList = string.Join(",", argTypeNames);
            return $"{functionName}({typeList})";
        }

        public byte[][] Decode(AbiSignature signature, byte[] data)
        {
            string[] argTypeNames = new string[signature.Types.Length];
            for (int i = 0; i < signature.Types.Length; i++)
            {
                argTypeNames[i] = signature.Types[i].ToString();
            }

            if (!Bytes.UnsafeCompare(data.Slice(0, 4), ComputeAddress(signature)))
            {
                throw new AbiException(
                    $"Signature in encoded ABI data is not consistent with {ComputeSignature(signature.Name, signature.Types)}");
            }

            int position = 4;
            byte[][] arguments = new byte[signature.Types.Length][];
            for (int i = 0; i < signature.Types.Length; i++)
            {
                (arguments[i], position) = signature.Types[i].Decode(data, position);
            }

            if (position != data.Length)
            {
                throw new AbiException($"Unexpected data at position {position}");
            }

            return arguments;
        }
    }
}