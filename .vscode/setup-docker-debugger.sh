set -e
echo ">>> Setting up debugger..."
apt-get install git curl -y
curl -sSL https://aka.ms/getvsdbgsh | /bin/sh /dev/stdin -v latest -l /root/vsdbg
echo ">>> Fetching sources..."
git clone https://github.com/NethermindEth/nethermind.git --depth=1 /src
grep --text -aoP 'Commit\((.*)\)' nethermind.dll | tr -d '\0' | sed 's/.*(\(.*\))/\1/' > /tmp/commit.txt
echo ">>> Checking commit $(cat /tmp/commit.txt)"
cd /src
git checkout -q $(cat /tmp/commit.txt)
echo '>>> Setup completed'
