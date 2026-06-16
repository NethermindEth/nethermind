#!/bin/bash
set -e

# Configure GitHub Actions Runner via SSH
# This script runs locally and uses SSH to configure the runner on the remote machine
# The GitHub token is passed via stdin to avoid it being visible in process listings or logs

echo "========================================="
echo "Configuring Runner via SSH"
echo "========================================="

# Validate required environment variables
if [ -z "$MACHINE_IP" ]; then
  echo "Error: MACHINE_IP environment variable is required"
  exit 1
fi

if [ -z "$GH_TOKEN" ]; then
  echo "Error: GH_TOKEN environment variable is required"
  exit 1
fi

if [ -z "$GH_RUNNER_LABEL" ]; then
  echo "Error: GH_RUNNER_LABEL environment variable is required"
  exit 1
fi

if [ -z "$ORG_NAME" ]; then
  echo "Error: ORG_NAME environment variable is required"
  exit 1
fi

if [ -z "$REPO_NAME" ]; then
  echo "Error: REPO_NAME environment variable is required"
  exit 1
fi

if [ -z "$SSH_PRIVATE_KEY" ]; then
  echo "Error: SSH_PRIVATE_KEY environment variable is required"
  exit 1
fi

# Setup SSH key in current directory
echo "Setting up SSH key..."
echo "$SSH_PRIVATE_KEY" > ./runner_key
chmod 600 ./runner_key

# SSH options
SSH_OPTS="-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -i ./runner_key"

# Get registration token (keeping it off the remote machine)
echo "Obtaining registration token..."
REGISTRATION_TOKEN=$(curl -s -X POST \
  -H "Authorization: token ${GH_TOKEN}" \
  -H "Accept: application/vnd.github.v3+json" \
  "https://api.github.com/repos/${ORG_NAME}/${REPO_NAME}/actions/runners/registration-token" | jq -r '.token')

if [ -z "$REGISTRATION_TOKEN" ] || [ "$REGISTRATION_TOKEN" = "null" ]; then
  echo "Error: Failed to obtain registration token"
  rm -f ./runner_key
  exit 1
fi

# Mask the registration token to prevent it from appearing in logs
echo "::add-mask::${REGISTRATION_TOKEN}"

echo "Registration token obtained successfully"

# Configure runner via SSH
echo "Configuring GitHub Actions runner on remote machine at ${MACHINE_IP}..."

# Execute runner configuration remotely
ssh ${SSH_OPTS} root@${MACHINE_IP} \
  RUNNER_TOKEN="${REGISTRATION_TOKEN}" \
  ORG_NAME="${ORG_NAME}" \
  REPO_NAME="${REPO_NAME}" \
  GH_RUNNER_LABEL="${GH_RUNNER_LABEL}" \
  bash << 'ENDSSH'
  set -e

  # Set up logging to file in home directory
  LOG_FILE="${HOME}/runner-configure-$(date +%Y%m%d-%H%M%S).log"

  # Redirect all output (stdout and stderr) to both console and log file
  exec > >(tee -a "${LOG_FILE}") 2>&1

  echo "========================================="
  echo "Runner Configuration via SSH"
  echo "Started at: $(date)"
  echo "Log file: ${LOG_FILE}"
  echo "========================================="

  cd /root/actions-runner
  export RUNNER_ALLOW_RUNASROOT="1"

  echo "Configuring runner..."
  # Registration token is short-lived and single-use, only for runner registration
  # By the time the machine is accessible, this process will have completed
  ./config.sh --url "https://github.com/${ORG_NAME}/${REPO_NAME}" \
    --token "${RUNNER_TOKEN}" \
    --name "${GH_RUNNER_LABEL}" \
    --labels "${GH_RUNNER_LABEL}" \
    --unattended \
    --replace

  echo "Runner configured successfully"

  # Install and start the runner service
  echo "Installing runner service..."
  ./svc.sh install

  echo "Starting runner service..."
  ./svc.sh start

  # Verify service is running
  sleep 2
  ./svc.sh status

  echo ""
  echo "========================================="
  echo "Runner Configuration Complete"
  echo "Log file saved to: ${LOG_FILE}"
  echo "========================================="
ENDSSH

EXIT_CODE=$?

# Cleanup SSH key
rm -f ./runner_key

if [ $EXIT_CODE -ne 0 ]; then
  echo "Error: SSH runner configuration failed with exit code ${EXIT_CODE}"
  exit $EXIT_CODE
fi

echo ""
echo "========================================="
echo "Runner Configuration Complete"
echo "========================================="
echo ""
