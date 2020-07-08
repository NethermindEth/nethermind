cd ~/repo_pub/nethermind
TAG="$(git describe --tags --always | cut -d- -f1)"
PAT_GITHUB="{{ Place for GitHub Token }}"
echo $TAG

# Sending dispatch event to GitHub Actions
curl -v -X POST -u $PAT_GITHUB -H "Accept: application/vnd.github.everest-preview+json" -H "Content-Type: application/json" --data '{"event_type":"docker_amd64", "client_payload": { "tag":"'"$TAG"'"}}' https://api.github.com/repos/nethermindeth/nethermind/dispatches
curl -v -X POST -u $PAT_GITHUB -H "Accept: application/vnd.github.everest-preview+json" -H "Content-Type: application/json" --data '{"event_type":"docker_arm64", "client_payload": { "tag":"'"$TAG"'"}}' https://api.github.com/repos/nethermindeth/nethermind/dispatches
curl -v -X POST -u $PAT_GITHUB -H "Accept: application/vnd.github.everest-preview+json" -H "Content-Type: application/json" --data '{"event_type":"docker_arm32", "client_payload": { "tag":"'"$TAG"'"}}' https://api.github.com/repos/nethermindeth/nethermind/dispatches