GIT_HASH="$(tail ./nethermind-packages/git-hash.txt)"
GIT_TAG="$(tail ./nethermind-packages/git-tag.txt)"
curl -X POST -H 'Content-type: application/json' --data '{"attachments":[{"color":"#36a64f","pretext":"Building and uploading packages has been finished.","title":"Azure Storage","title_link":"https:\/\/portal.azure.com\/#@4da0a42f-f650-4309-b3de-c95bf171b70e\/resource\/subscriptions\/555a2b00-fcef-4358-92da-b9eae07a501f\/resourceGroups\/neth-dev-rg\/providers\/Microsoft.Storage\/storageAccounts\/nethdev\/containersList","fields":[{"title":"version","value":"'$GIT_TAG-$GIT_HASH'","short":false},{"title":"repo","value":"Public","short":false}]}]}' $PUB_WEBHOOK_URL

