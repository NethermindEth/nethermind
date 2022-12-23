// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Containers
{
    public class Ref<T> : IEquatable<Ref<T>> where T : class
    {
        public Ref(T item)
        {
            Item = item;
            Root = null;
        }

        public T Item { get; set; }

        public Root? Root { get; set; }



        // for Item we can describe the location -> Memory / DB / other?
        // ChangeLocation(TargetLocation) -> this way we can move from memory to the database and back - use object or ssz format for each location
        public bool Equals(Ref<T>? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Item.Equals(other.Item) || (Root?.Equals(other.Root) ?? false);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((Ref<T>)obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Item, Root);
        }
    }
}
