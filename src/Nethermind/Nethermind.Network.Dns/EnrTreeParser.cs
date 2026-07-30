// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Dns;

public static class EnrTreeParser
{
    private const string EnrPrefix = "enr:";
    private const string EnrLinkedTreePrefix = "enrtree://";

    private const int HashLengthBase32 = 26;
    private const int SigLengthBase32 = 87;

    private const int HashesIndex = 15; // "enrtree-branch:".Length;

    public static EnrTreeNode ParseNode(string enrTreeNodeText)
    {
        if (enrTreeNodeText.StartsWith("enrtree-branch:"))
        {
            return ParseBranch(enrTreeNodeText);
        }

        if (enrTreeNodeText.StartsWith("enrtree-root:"))
        {
            return ParseEnrRoot(enrTreeNodeText);
        }

        if (enrTreeNodeText.StartsWith(EnrPrefix))
        {
            return ParseEnrLeaf(enrTreeNodeText);
        }

        if (enrTreeNodeText.StartsWith(EnrLinkedTreePrefix))
        {
            return ParseEnrLinkedTree(enrTreeNodeText);
        }

        throw new NotSupportedException("ENR tree node type not supported: " + enrTreeNodeText);
    }

    private static EnrLinkedTree ParseEnrLinkedTree(string enrLinkedTreeText)
    {
        EnrLinkedTree leaf = new();
        leaf.Link = enrLinkedTreeText[EnrLinkedTreePrefix.Length..];
        return leaf;
    }

    private static EnrLeaf ParseEnrLeaf(string enrLeafText)
    {
        EnrLeaf leaf = new();
        leaf.NodeRecord = enrLeafText[EnrPrefix.Length..];
        return leaf;
    }

    public static EnrTreeBranch ParseBranch(string enrTreeBranchText)
    {
        EnrTreeBranch branch = new();
        branch.Hashes = enrTreeBranchText[HashesIndex..].Split(',');
        return branch;
    }

    public static EnrTreeRoot ParseEnrRoot(string enrTreeRootText)
    {
        ArgumentNullException.ThrowIfNull(enrTreeRootText);

        // enrTreeRootText comes from an untrusted DNS TXT record (parsed before any signature check),
        // so validate every field's presence and length before slicing to avoid an unhandled exception.
        EnrTreeRoot enrTreeRoot = new()
        {
            EnrRoot = ExtractFixedField(enrTreeRootText, "e=", HashLengthBase32),
            LinkRoot = ExtractFixedField(enrTreeRootText, "l=", HashLengthBase32),
            Sequence = ExtractSequence(enrTreeRootText),
            Signature = ExtractFixedField(enrTreeRootText, "sig=", SigLengthBase32),
        };

        return enrTreeRoot;

        static string ExtractFixedField(string text, string key, int length)
        {
            int index = text.IndexOf(key, StringComparison.Ordinal);
            if (index < 0 || index + key.Length + length > text.Length)
            {
                throw new FormatException($"Malformed enrtree-root: '{key}' field is missing or too short.");
            }

            if (text.IndexOf(key, index + key.Length, StringComparison.Ordinal) >= 0)
            {
                throw new FormatException($"Malformed enrtree-root: '{key}' field appears more than once.");
            }

            return text.Substring(index + key.Length, length);
        }

        static int ExtractSequence(string text)
        {
            int index = text.IndexOf("seq=", StringComparison.Ordinal);
            if (index < 0)
            {
                throw new FormatException("Malformed enrtree-root: 'seq=' field is missing.");
            }

            if (text.IndexOf("seq=", index + "seq=".Length, StringComparison.Ordinal) >= 0)
            {
                throw new FormatException("Malformed enrtree-root: 'seq=' field appears more than once.");
            }

            int start = index + "seq=".Length;
            int end = text.IndexOf(' ', start);
            if (end < 0)
            {
                throw new FormatException("Malformed enrtree-root: 'seq=' field is not terminated.");
            }

            if (!int.TryParse(text.AsSpan(start, end - start), out int sequence))
            {
                throw new FormatException("Malformed enrtree-root: 'seq=' value is not a valid number.");
            }

            return sequence;
        }
    }
}
