/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;

namespace Nethermind.Core.Crypto.ZkSnarks
{
    /// <summary>
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
    /// </summary>
    public interface IField<T> : IEquatable<T>
    {
        T Add(T o);
        T Mul(T o);
        T MulByNonResidue();
        T Sub(T o);
        T Squared();
        T Double();
        T Inverse();
        T Negate();
        bool IsZero();
        bool IsValid();
    }

    public static class FieldParams<T>
    {
        public static T Zero { get; internal set; }
        public static T One { get; internal set; }
    }
}