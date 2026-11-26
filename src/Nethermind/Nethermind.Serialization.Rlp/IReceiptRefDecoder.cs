// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp;

public interface IReceiptRefDecoder
{
    void DecodeStructRef(scoped ref RlpReader decoderContext, RlpBehaviors rlpBehaviors, out TxReceiptStructRef item);
    void DecodeLogEntryStructRef(scoped ref RlpReader decoderContext, RlpBehaviors none, out LogEntryStructRef current);
    Hash256[] DecodeTopics(RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
    bool CanDecodeBloom { get; }
}
