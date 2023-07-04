// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network
{
    public interface IMessageSerializer<T> where T : MessageBase
    {
        byte[] Serialize(T msg);
        T Deserialize(byte[] msgBytes);
    }
}
