echo =======================================================
echo Building Nethermind packages
echo =======================================================

./pull-all.sh && ./setup-packages.sh && ./build-runner.sh && ./build-cli.sh && ./build-launcher.sh && ./archive-packages.sh && ./azure-upload.sh && ./publish-packages.sh && ./slack-poster.sh

echo =======================================================
echo Building Nethermind completed
echo =======================================================
