#!/usr/bin/env bash
set -euo pipefail

MD_URL=http://metadata.google.internal/computeMetadata/v1
md() { curl -sf -H 'Metadata-Flavor: Google' "$MD_URL/instance/attributes/$1"; }
stage() {
  echo "=== stage: $1 ==="
  curl -sf -X PUT --data "$1" -H 'Metadata-Flavor: Google' \
    "$MD_URL/instance/guest-attributes/gh-runner/stage" >/dev/null 2>&1 || true
}
fail() {
  local rc=$?
  curl -sf -X PUT --data "line ${BASH_LINENO[0]}: exit ${rc}" -H 'Metadata-Flavor: Google' \
    "$MD_URL/instance/guest-attributes/gh-runner/error" >/dev/null 2>&1 || true
  stage failed
  exit "$rc"
}
trap fail ERR

stage starting

DATA_MOUNT="$(md data-mount || echo /mnt/gh-runner)"
RUNNER_DIR="${DATA_MOUNT}/actions-runner"
RUNNER_HOME="${DATA_MOUNT}/home"
DOCKER_ROOT="${DATA_MOUNT}/docker"
RUNNER_VERSION="$(md runner-version)"

export DEBIAN_FRONTEND=noninteractive
APT='apt-get -o DPkg::Lock::Timeout=900 -y'

# unattended-upgrades fires ~1min after boot and holds the dpkg lock, which breaks both
# this script and the sync job's own apt-get install later in the run.
stage apt-quiesce
systemctl stop unattended-upgrades.service 2>/dev/null || true
systemctl disable --now apt-daily.timer apt-daily-upgrade.timer 2>/dev/null || true
systemctl stop apt-daily.service apt-daily-upgrade.service 2>/dev/null || true

stage disks
# google-local-nvme-ssd-* is the only naming that reliably excludes the boot disk: on some
# machine families the boot persistent disk is itself presented as /dev/nvme0n1.
SSDS=()
for dev in /dev/disk/by-id/google-local-nvme-ssd-*; do
  [ -e "$dev" ] || continue
  [[ "$dev" == *-part[0-9]* ]] && continue
  SSDS+=("$dev")
done
echo "local NVMe SSDs: ${#SSDS[@]} -> ${SSDS[*]:-none}"
mkdir -p "$DATA_MOUNT"

MKFS_OPTS=(-F -m 0 -E lazy_itable_init=1,lazy_journal_init=1)
MOUNT_OPTS='discard,defaults,noatime'

if (( ${#SSDS[@]} == 0 )); then
  echo "WARNING: no local SSD attached, falling back to the boot disk for ${DATA_MOUNT}"
elif (( ${#SSDS[@]} == 1 )); then
  mkfs.ext4 "${MKFS_OPTS[@]}" "${SSDS[0]}"
  mount -o "$MOUNT_OPTS" "${SSDS[0]}" "$DATA_MOUNT"
else
  $APT install --no-install-recommends mdadm
  mdadm --create /dev/md0 --level=0 --force --raid-devices="${#SSDS[@]}" "${SSDS[@]}"
  mkfs.ext4 "${MKFS_OPTS[@]}" /dev/md0
  mount -o "$MOUNT_OPTS" /dev/md0 "$DATA_MOUNT"
fi

chmod 0755 "$DATA_MOUNT"
mkdir -p "$RUNNER_DIR" "$RUNNER_HOME" "$DOCKER_ROOT"
df -h "$DATA_MOUNT"

stage docker
install -d -m 0755 /etc/docker
# Written before docker-ce is installed: the postinst starts dockerd immediately, and a
# data-root set afterwards leaves chain data on the boot disk until it fills mid-sync.
cat > /etc/docker/daemon.json <<EOF
{
  "data-root": "${DOCKER_ROOT}",
  "storage-driver": "overlay2",
  "log-driver": "json-file",
  "log-opts": { "max-size": "100m", "max-file": "3" }
}
EOF

install -d -m 0755 /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
. /etc/os-release
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" \
  > /etc/apt/sources.list.d/docker.list
$APT update
$APT install docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
systemctl enable --now docker

DOCKER_ROOT_ACTUAL=$(docker info --format '{{.DockerRootDir}}')
if [ "$DOCKER_ROOT_ACTUAL" != "$DOCKER_ROOT" ]; then
  echo "ERROR: docker root is ${DOCKER_ROOT_ACTUAL}, expected ${DOCKER_ROOT}"
  exit 1
fi

stage tools
$APT install ca-certificates curl jq git git-lfs unzip zip tar \
             make build-essential screen lshw dmidecode fio python3 python3-venv

stage runner-install
cd "$RUNNER_DIR"
curl -fsSL -o runner.tar.gz \
  "https://github.com/actions/runner/releases/download/v${RUNNER_VERSION}/actions-runner-linux-x64-${RUNNER_VERSION}.tar.gz"
tar xzf runner.tar.gz && rm -f runner.tar.gz
./bin/installdependencies.sh

stage runner-start
# Single-use credential: it is already visible in instance metadata to anyone holding
# compute.instances.get, and run.sh puts it in Runner.Listener's argv regardless.
umask 077
md runner-jit-config > /etc/gh-runner.jitconfig
umask 022

cat > /usr/local/bin/gh-runner-start <<EOF
#!/bin/sh
exec "${RUNNER_DIR}/run.sh" --jitconfig "\$(cat /etc/gh-runner.jitconfig)"
EOF
chmod 0755 /usr/local/bin/gh-runner-start

cat > /etc/systemd/system/gh-runner.service <<EOF
[Unit]
Description=GitHub Actions ephemeral JIT runner
After=network-online.target docker.service
Requires=docker.service
Wants=network-online.target

[Service]
Type=simple
User=root
WorkingDirectory=${RUNNER_DIR}
Environment=HOME=${RUNNER_HOME}
Environment=RUNNER_ALLOW_RUNASROOT=1
ExecStart=/usr/local/bin/gh-runner-start
Restart=no
KillMode=process
TimeoutStopSec=60

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl start gh-runner.service
stage runner-started
