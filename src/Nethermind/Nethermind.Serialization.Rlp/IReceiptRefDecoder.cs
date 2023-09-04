// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp;

public interface IReceiptRefDecoder
{
    void DecodeStructRef(scoped ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors, out TxReceiptStructRef item);
    void DecodeLogEntryStructRef(scoped ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors none, out LogEntryStructRef current);
    Keccak[] DecodeTopics(Rlp.ValueDecoderContext valueDecoderContext);
    bool CanDecodeBloom { get; }
}
