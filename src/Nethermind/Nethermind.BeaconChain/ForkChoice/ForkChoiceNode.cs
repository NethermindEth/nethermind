// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>A block and which of its fork-choice nodes is meant: the spec's <c>ForkChoiceNode</c> (specs/gloas/fork-choice.md).</summary>
public readonly record struct ForkChoiceNode(Hash256 Root, ForkChoicePayloadStatus PayloadStatus);
