echo =======================================================
echo Building Nethermind packages
echo =======================================================

./setup-packages.sh && ./build-runner.sh && ./build-launcher.sh && ./archive-packages.sh

echo =======================================================
echo Building Nethermind completed
echo =======================================================