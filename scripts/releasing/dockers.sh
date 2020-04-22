cd ~/repo_pub/nethermind
TAG="$(git describe --tags --always | cut -d- -f1)"
echo $TAG
# Sending dispatch event to GitHub Actions
curl -v -X POST -u "{{ Place for GitHub Token}}" -H "Accept: application/vnd.github.everest-preview+json" -H "Content-Type: application/json" --data '{"event_type":"docker_amd64_alpine", "client_payload": { "tag":"'"$TAG"'"}}' https://api.github.com/repos/nethermindeth/nethermind/dispatches
curl -v -X POST -u "{{ Place for GitHub Token}}" -H "Accept: application/vnd.github.everest-preview+json" -H "Content-Type: application/json" --data '{"event_type":"docker_amd64_debian", "client_payload": { "tag":"'"$TAG"'"}}' https://api.github.com/repos/nethermindeth/nethermind/dispatches
curl -v -X POST -u "{{ Place for GitHub Token}}" -H "Accept: application/vnd.github.everest-preview+json" -H "Content-Type: application/json" --data '{"event_type":"docker_arm64_alpine", "client_payload": { "tag":"'"$TAG"'"}}' https://api.github.com/repos/nethermindeth/nethermind/dispatches
curl -v -X POST -u "{{ Place for GitHub Token}}" -H "Accept: application/vnd.github.everest-preview+json" -H "Content-Type: application/json" --data '{"event_type":"docker_arm64_debian", "client_payload": { "tag":"'"$TAG"'"}}' https://api.github.com/repos/nethermindeth/nethermind/dispatches
curl -v -X POST -u "{{ Place for GitHub Token}}" -H "Accept: application/vnd.github.everest-preview+json" -H "Content-Type: application/json" --data '{"event_type":"docker_arm32_debian", "client_payload": { "tag":"'"$TAG"'"}}' https://api.github.com/repos/nethermindeth/nethermind/dispatches
