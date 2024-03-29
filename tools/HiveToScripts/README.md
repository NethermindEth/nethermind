# Hive test run to scripts

Hive produces verbose output while running tests, it's enough to reproduce test cases by running queries logged by the runner.
The only barrier is log trimming, which is turned off in flcl42/hive repo and will be soon disabled in case of **log level > 3** in the main hive repo.
So to extract test cases in form of script, the following needs to be done:

0. In case of pipeline usage download the logs and unpack them
1. Confirm the logs were not trimmed
2. Typically the directory has a `logs` entry that contains `*-simulator-*.log` files - it's the first input for the tool<br>
   Output directory and JSONRPC URL should be passed also:
   ```
   dotnet build .
   .\bin\Debug\net8.0\HiveToScripts.exe T:\logs T:\exp http://localhost:8550
   ```
3. The tool will generate:
   - A l ist of tests and their containers is in`__tests.txt`
   - '.ps1/.sh/.http' files, that are named after containers run by hive and big data alongside
   - Configs and specifications extracted from container logs
4. Start execution client, passing location of config and spec(use launchsettings.json for the runner or jsut pass the args):
   ```nethermind -c T:\\output\\0c0ffee0.cfg --Init.ChainSpecPath=T:\\output\\0c0ffee0.json --log TRACE``
5. Execute the corresponding scripts. GL!
