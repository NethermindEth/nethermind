// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc;

/// <summary>Selects how many top-level JSON documents the inbound parser accepts from one transport payload.</summary>
public enum JsonRpcInputMode
{
    /// <summary>Accepts exactly one top-level JSON document and rejects trailing non-whitespace data.</summary>
    SingleDocument,

    /// <summary>Accepts multiple adjacent top-level JSON documents from one framed transport payload.</summary>
    MultipleDocuments
}
