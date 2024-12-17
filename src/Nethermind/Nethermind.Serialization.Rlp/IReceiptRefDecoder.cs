// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp;

public interface IReceiptRefDecoder
{
    void DecodeStructRef(scoped ref RlpValueStream rlpStream, RlpBehaviors rlpBehaviors, out TxReceiptStructRef item);
    void DecodeLogEntryStructRef(scoped ref RlpValueStream rlpStream, RlpBehaviors none, out LogEntryStructRef current);
    Hash256[] DecodeTopics(RlpValueStream valueDecoderContext);
    bool CanDecodeBloom { get; }
}
