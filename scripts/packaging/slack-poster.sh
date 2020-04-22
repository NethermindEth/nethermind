GIT_HASH="$(tail ./nethermind-packages/git-hash.txt)"
GIT_TAG="$(tail ./nethermind-packages/git-tag.txt)"
# change title link to Azure containers endpoint
curl -X POST -H 'Content-type: application/json' --data '{"attachments":[{"color":"#36a64f","pretext":"Building and uploading packages has been finished.","title":"Azure Storage","title_link":"{{ Place link to Azure containers }}","fields":[{"title":"version","value":"'$GIT_TAG-$GIT_HASH'","short":false},{"title":"repo","value":"Public","short":false}]}]}' $PRIVATE_WEBHOOK_URL
