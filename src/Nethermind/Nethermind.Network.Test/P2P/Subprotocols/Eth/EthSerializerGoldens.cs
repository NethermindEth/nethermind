// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth;

/// <summary>
/// Hand-derived RLP goldens shared by the eth serializer tests.
/// </summary>
/// <remarks>
/// The values are derived from the RLP rules and verified with an independent
/// encoder (pyrlp + pycryptodome keccak), not captured from Nethermind output.
/// </remarks>
internal static class EthSerializerGoldens
{
    /// <summary>An empty message payload: the empty RLP list.</summary>
    public const string EmptyListRlp = "c0";

    /// <summary>
    /// RLP of [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC]:
    /// three 0xa0 + 32-byte items - a 99-byte list payload (0xf8 0x63).
    /// </summary>
    public const string KeccakAbcListRlp =
        "f863a003783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760a01f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111a0017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72";
}
