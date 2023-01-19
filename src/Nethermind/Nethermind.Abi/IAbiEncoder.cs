// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Abi
{
    public interface IAbiEncoder
    {
        /// <summary>
        /// Encodes data in accordance to the Solidity ABI encoding.
        /// </summary>
        /// <param name="encodingStyle">One of the ABI styles.</param>
        /// <param name="signature">The signature of the Solidity method.</param>
        /// <param name="arguments">Arguments of the Solidity method.</param>
        /// <returns>ABI encoded data.</returns>
        byte[] Encode(AbiEncodingStyle encodingStyle, AbiSignature signature, params object[] arguments);

        /// <summary>
        /// Decodes ABI encoded data into a set of objects.
        /// </summary>
        /// <param name="encodingStyle">One of the ABI styles.</param>
        /// <param name="signature">Signature of the Solidity method for which the arguments were passed.</param>
        /// <param name="data">ABI encoded data.</param>
        /// <returns></returns>
        object[] Decode(AbiEncodingStyle encodingStyle, AbiSignature signature, byte[] data);
    }
}
