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

using System.IO;
using NUnit.Framework;

namespace Ethereum2.Bls.Test
{
    public class BlsTests
    {
        [Test]
        public void Bls_aggregate_pubkeys()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("aggregate_pubkeys", "small"));
        }
        
        [Test]
        public void Bls_aggregate_sigs()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("aggregate_sigs", "small"));
        }
        
        [Test]
        public void Bls_msg_hash_compressed()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("msg_hash_compressed", "small"));
        }
        
        [Test]
        public void Bls_msg_hash_uncompressed()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("msg_hash_uncompressed", "small"));
        }
        
        [Test]
        public void Bls_priv_to_pub()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("priv_to_pub", "small"));
        }
        
        [Test]
        public void Bls_sign_msg()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("sign_msg", "small"));
        }
    }
}