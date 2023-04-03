// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Dns;

public static class EnrTreeParser
{
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

        if (enrTreeNodeText.StartsWith("enr:"))
        {
            return ParseEnrLeaf(enrTreeNodeText);
        }

        if (enrTreeNodeText.StartsWith("enrtree://"))
        {
            return ParseEnrLinkedTree(enrTreeNodeText);
        }

        throw new NotSupportedException("ENR tree node type not supported: " + enrTreeNodeText);
    }

    private static EnrLinkedTree ParseEnrLinkedTree(string enrLinkedTreeText)
    {
        EnrLinkedTree leaf = new();
        leaf.Link = enrLinkedTreeText[10..];
        return leaf;
    }

    private static EnrLeaf ParseEnrLeaf(string enrLeafText)
    {
        EnrLeaf leaf = new();
        leaf.NodeRecord = enrLeafText;
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
        if (enrTreeRootText is null) throw new ArgumentNullException(nameof(enrTreeRootText));

        EnrTreeRoot enrTreeRoot = new();

        int ensRootIndex = enrTreeRootText.IndexOf("e=", StringComparison.InvariantCulture);
        enrTreeRoot.EnrRoot = enrTreeRootText.Substring(ensRootIndex + 2, HashLengthBase32);

        int linkRootIndex = enrTreeRootText.IndexOf("l=", StringComparison.InvariantCulture);
        enrTreeRoot.LinkRoot = enrTreeRootText.Substring(linkRootIndex + 2, HashLengthBase32);

        int seqIndex = enrTreeRootText.IndexOf("seq=", StringComparison.InvariantCulture);
        int seqLength = enrTreeRootText.IndexOf(" ", seqIndex, StringComparison.InvariantCulture) - (seqIndex + 4);
        enrTreeRoot.Sequence = int.Parse(enrTreeRootText.AsSpan(seqIndex + 4, seqLength));

        int sigIndex = enrTreeRootText.IndexOf("sig=", StringComparison.InvariantCulture);
        enrTreeRoot.Signature = enrTreeRootText.Substring(sigIndex + 4, SigLengthBase32);

        return enrTreeRoot;
    }
}
