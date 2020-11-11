//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Vault.Config;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Vault.Test
{
    public class VaultTxSenderTests
    {
        [Test]
        public void Can_Initialize_VaultTxSender_without_exceptions()
        {
            var vaultConfig = new VaultConfig();
            vaultConfig.VaultId = "1b16996e-3595-4985-816c-043345d22f8c";
            var _vaultService = new VaultService(vaultConfig, LimboLogs.Instance);

            IVaultWallet wallet = new VaultWallet(_vaultService, vaultConfig.VaultId, LimboLogs.Instance);
            ITxSigner vaultSigner = new VaultTxSigner(wallet, 1);
            Assert.DoesNotThrow(() => { new VaultTxSender(vaultSigner, vaultConfig, 1); });
        }

        
/*
        [Test]
        public async Task Can_send_nchain_post_request()
        {
            var vaultConfig = new VaultConfig();
            vaultConfig.NChainHost = "localhost:8080";
            vaultConfig.NChainPath = "api/v1";
            vaultConfig.NChainToken = $"eyJhbGciOiJSUzI1NiIsImtpZCI6IjEwOjJlOmQ5OmUxOmI4OmEyOjM0OjM3Ojk5OjNhOjI0OmZjOmFhOmQxOmM4OjU5IiwidHlwIjoiSldUIn0.eyJhdWQiOiJodHRwOi8vbG9jYWxob3N0OjgwODEvYXBpL3YxIiwiZXhwIjoxNjA1MTc0NDUzLCJpYXQiOjE2MDUwODgwNTMsImlzcyI6Imh0dHBzOi8vaWRlbnQucHJvdmlkZS5zZXJ2aWNlcyIsImp0aSI6ImY3YzY5ZTMwLWU0MTItNDU3MC1hYzk3LTY3ZmJlMzQ4YmYxNiIsIm5hdHMiOnsicGVybWlzc2lvbnMiOnsic3Vic2NyaWJlIjp7ImFsbG93IjpbInVzZXIuOTZiODVkMjItMzI1Yi00YThmLTg1MzItYWVkNmQ2YTNkMzE1IiwibmV0d29yay4qLmNvbm5lY3Rvci4qIiwibmV0d29yay4qLnN0YXR1cyIsInBsYXRmb3JtLlx1MDAzZSJdfX19LCJwcnZkIjp7InBlcm1pc3Npb25zIjo3NTUzLCJ1c2VyX2lkIjoiOTZiODVkMjItMzI1Yi00YThmLTg1MzItYWVkNmQ2YTNkMzE1In0sInN1YiI6InVzZXI6OTZiODVkMjItMzI1Yi00YThmLTg1MzItYWVkNmQ2YTNkMzE1In0.dNeoJeBKuALTZkxWFOXvpI7kO2QLO7XpwxRVz2cbtlWInpiAAgr5N5mxemEURPIL61VmGI4bk-dnSpqn29sW8F2g8Qvk6DdhLpgETJzfOWd6F_lWmyDxOV75pJOuuTeqcP_FYsxnE6Q5yLlMCYlmHOQ28ixil7Zvx1XhKkKTaBeYCGAwXGdJxIFk-69eRGpJVb_pB62yzmeOaKUaR_cUOvD5Aczuucq_oc73tCMfDZGnu9aV5YUghFkVL90nBRzmKeNbYQXk3wByeM_ToCkMplxnqkaqxjiRIhY30ka13awa0aMpiW_Q23aOrTFEzrGmBpcZ-HHWkGrscSIP2fYjFqVvkC-pLhK3M97UcpN5xk5ys4OrQm9kNB3gUZD-nsCUoXQlAxIg7BGBy-yjymsVZ-h69fnv3lcoQ-x0nICmAkuB3N3sVuLPYabUKTnudC3rBqzU7dxH_g4W4urNms-2Nxy-YsE7YAF5L9uQqbLFLEpqErvVBgK_UHWY1UIJBaoMnGPN8jG7DIrTW1zscikwoLTb0VFHLookaTzT5YS3vznmqQSf6VPY05quviBlbJ4FTigPyECwLV-sJUc__58WSCyQxqGE4MeX7BvQxnxWQCDEXNf0_ftd1ecJZkmx33erTswO6rsv8C6EzS6AHPLgXP5KnnnZm_f7VF4jVlmEGes";
            vaultConfig.NChainScheme = "http";
            var _vaultService = new VaultService(vaultConfig, LimboLogs.Instance);

            IVaultWallet wallet = new VaultWallet(_vaultService, vaultConfig.VaultId, LimboLogs.Instance);
            ITxSigner vaultSigner = new VaultTxSigner(wallet, 1);
            var txSender = new VaultTxSender(vaultSigner, vaultConfig, 1);
            var result = await txSender.SendPostNChainRequest("accounts", @"{""network_id"":""9a2dd9ce-d283-4766-9ce5-c84a30474121""}"" }");
        }
        */
    }
}
