using System;
using System.IO;
using System.Security;
using Nethermind.Crypto;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;

namespace Nethermind.KeyStore
{
    public class PasswordProvider : IPasswordProvider
    {
        private readonly IKeyStoreConfig _keyStoreConfig;
        public PasswordProvider(IKeyStoreConfig keyStoreConfig)
        {
            _keyStoreConfig = keyStoreConfig ?? throw new ArgumentNullException(nameof(keyStoreConfig));
        }
        public SecureString GetBlockAuthorPassword()
        {
            SecureString passwordFromFile = null;
            var index = Array.IndexOf(_keyStoreConfig.UnlockAccounts, _keyStoreConfig.BlockAuthorAccount);
            if (index >= 0)
            {
                passwordFromFile = GetPassword(index);
            }

            return passwordFromFile != null ? passwordFromFile : GetPasswordFromConsole();
        }

        public SecureString GetPassword(int keyStoreConfigPasswordIndex)
        {
            string GetPasswordN(int n, string[] passwordsCollection) => passwordsCollection?.Length > 0 ? passwordsCollection[Math.Min(n, passwordsCollection.Length - 1)] : null;

            SecureString password = null;
            var passwordFile = GetPasswordN(keyStoreConfigPasswordIndex, _keyStoreConfig.PasswordFiles);
            if (passwordFile != null)
            {
                string blockAuthorPasswordFilePath = passwordFile.GetApplicationResourcePath();
                password = File.Exists(blockAuthorPasswordFilePath)
                    ? ReadFromFileToSecureString(blockAuthorPasswordFilePath)
                    : null;
            }

            password ??= GetPasswordN(keyStoreConfigPasswordIndex, _keyStoreConfig.Passwords).Secure();
            return password;
        }

        public SecureString GetPasswordFromConsole()
        {
            return ConsoleUtils.ReadSecret($"Provide password for validator account {_keyStoreConfig.BlockAuthorAccount}");
        }

        private SecureString ReadFromFileToSecureString(string filePath)
        {
            var secureString = new SecureString();
            using (StreamReader stream = new StreamReader(filePath))
            {
                while (stream.Peek() >= 0)
                {
                    var character = (char)stream.Read();
                    secureString.AppendChar(character);
                }
            }

            return secureString;
        }
    }
}
