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

using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public class Bootnode
    {
        public Bootnode(string enode, string description)
        {
            //enode://0d837e193233c08d6950913bf69105096457fbe204679d6c6c021c36bb5ad83d167350440670e7fec189d80abc18076f45f44bfe480c85b6c632735463d34e4b@89.197.135.74:30303
            Hex publicKeyString = new Hex(enode.Substring(8, 128));
            PublicKey = new PublicKey(publicKeyString);
            string[] address = enode.Substring(8 /* prefix */ + 128 /* public key */ + 1 /* @ */).Split(':');
            Host = address[0];
            Port = int.Parse(address[1]);
            Description = description;
        }

        public Bootnode(Hex publicKey, string ip, int port, string description)
        {
            PublicKey = new PublicKey(publicKey);
            Host = ip;
            Port = port;
            Description = description;
        }

        public PublicKey PublicKey { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Description { get; set; }
    }
}