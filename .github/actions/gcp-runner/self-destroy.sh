#!/usr/bin/env bash
set -uo pipefail

MD_URL=http://metadata.google.internal/computeMetadata/v1
md() { curl -sf -H 'Metadata-Flavor: Google' "$MD_URL/instance/$1"; }

# Identity comes from the metadata server rather than a create-step output, because matrix
# strategies clobber job outputs and this runs on the instance being deleted.
NAME=$(md name) || { echo "::warning title=GCP runner::metadata server unreachable, leaving teardown to destroy_runner"; exit 0; }
ZONE=$(md zone | awk -F/ '{print $NF}')

# A direct API call rather than `gcloud compute instances delete`, which has no --async:
# the REST call returns once the operation is created, letting this job finish reporting
# before the VM disappears from under it. Mirrors the Linode destroy-machine-async step.
code=$(curl -sS -o /dev/null -w '%{http_code}' -X DELETE \
  -H "Authorization: Bearer ${ACCESS_TOKEN}" \
  "https://compute.googleapis.com/compute/v1/projects/${PROJECT_ID}/zones/${ZONE}/instances/${NAME}") \
  || code=000

case "$code" in
  200|204) echo "delete requested for ${NAME} in ${ZONE}" ;;
  404) echo "${NAME} is already gone" ;;
  *) echo "::warning title=GCP runner::self-delete of ${NAME} returned HTTP ${code}, leaving it to destroy_runner" ;;
esac

# Never fail the sync job over teardown; destroy_runner and --max-run-duration both remain.
exit 0
