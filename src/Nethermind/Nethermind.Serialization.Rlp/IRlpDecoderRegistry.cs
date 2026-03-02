// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp;

public interface IRlpDecoderRegistry
{
    IRlpValueDecoder<T>? GetValueDecoder<T>(string key = RlpDecoderKey.Default);
    IRlpStreamEncoder<T>? GetStreamEncoder<T>(string key = RlpDecoderKey.Default);
    IRlpObjectDecoder<T> GetObjectDecoder<T>(string key = RlpDecoderKey.Default);
}
