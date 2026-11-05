// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Text;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Network.Dns;

/// <summary>
/// The root of the tree is a TXT record with the following content:
/// enrtree-root:v1 e=[enr-root] l=[link-root] seq=[sequence-number] sig=[signature]
/// </summary>
public class EnrTreeRoot : EnrTreeNode
{
    private const int RecoverableSignatureLength = 65;

    /// <summary>
    /// the root hashes of subtrees containing nodes and links subtrees
    /// </summary>
    public string EnrRoot { get; set; } = string.Empty;

    /// <summary>
    /// the root hashes of subtrees containing nodes and links subtrees
    /// </summary>
    public string LinkRoot { get; set; } = string.Empty;

    /// <summary>
    /// Updated each time the tree gets updated.
    /// </summary>
    public ulong Sequence { get; set; }

    /// <summary>
    /// The base64url signature of the tree signer over the root content.
    /// </summary>
    public string Signature { get; set; } = string.Empty;

    public override string ToString() => $"{SignedContent} sig={Signature}";

    private string SignedContent => $"enrtree-root:v1 e={EnrRoot} l={LinkRoot} seq={Sequence}";

    /// <summary>
    /// Checks that <see cref="Signature"/> was produced by the tree signer whose compressed public key is <paramref name="signerPublicKey"/>.
    /// </summary>
    /// <remarks>
    /// EIP-1459 signs keccak256 of the root content without the <c>sig=</c> field. The recovery byte is ignored and both
    /// recovery ids are tried, matching the reference implementation, which verifies the 64-byte signature only.
    /// </remarks>
    internal bool IsSignedBy(ReadOnlySpan<byte> signerPublicKey)
    {
        Span<byte> signature = stackalloc byte[RecoverableSignatureLength];
        if (!Base64Url.TryDecodeFromChars(Signature, signature, out int length) || length != RecoverableSignatureLength)
        {
            return false;
        }

        ValueHash256 hash = ValueKeccak.Compute(SignedContent);
        Span<byte> recovered = stackalloc byte[CompressedPublicKey.LengthInBytes];
        for (int recoveryId = 0; recoveryId <= 1; recoveryId++)
        {
            if (SecP256k1.RecoverKeyFromCompact(recovered, hash.Bytes, signature[..^1], recoveryId, compressed: true) &&
                recovered.SequenceEqual(signerPublicKey))
            {
                return true;
            }
        }

        return false;
    }

    public override string[] Refs
    {
        get
        {
            return new[] { EnrRoot, LinkRoot };
        }
    }

    public override string[] Links => [];

    public override string[] Records => [];
}
