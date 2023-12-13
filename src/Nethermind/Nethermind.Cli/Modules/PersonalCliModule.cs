// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Jint.Native;

namespace Nethermind.Cli.Modules
{
    [CliModule("personal")]
    public class PersonalCliModule : CliModuleBase
    {
        [CliFunction("personal", "importRawKey")]
        public string? ImportRawKey(string keyData, string passphrase)
        {
            return NodeManager.Post<string>($"personal_importRawKey", keyData, passphrase).Result;
        }

        [CliProperty("personal", "listAccounts")]
        public JsValue ListAccounts()
        {
            return NodeManager.PostJint($"personal_listAccounts").Result;
        }

        [CliFunction("personal", "newAccount")]
        public string? NewAccount(string password)
        {
            return NodeManager.Post<string>($"personal_newAccount", password).Result;
        }

        [CliFunction("personal", "lockAccount")]
        public bool LockAccount(string addressHex)
        {
            return NodeManager.Post<bool>($"personal_lockAccount", addressHex).Result;
        }

        [CliFunction("personal", "unlockAccount")]
        public bool UnlockAccount(string addressHex, string password)
        {
            return NodeManager.Post<bool>($"personal_unlockAccount", addressHex, password).Result;
        }

        public PersonalCliModule(ICliEngine engine, INodeManager nodeManager) : base(engine, nodeManager)
        {
        }
    }
}
