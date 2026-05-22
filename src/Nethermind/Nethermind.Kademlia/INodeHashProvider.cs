// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.Kademlia;

/// <summary>
/// Just a convenient interface with only one generic parameter.
/// </summary>
/// <typeparam name="TNode"></typeparam>
public interface INodeHashProvider<in TNode>
{
    KademliaHash GetHash(TNode node);
}
