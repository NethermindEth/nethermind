Metrics
********

Nethermind metrics can be consumed by Prometheus/Grafana if configured in Metrics configuration categoru (check configuration documentation for details). Metrics then can be used to monitor running nodes.


Blockchain
^^^^^^^^^^


 nethermind_blocks
  Total number of blocks processed


 nethermind_mgas
  Total MGas processed


 nethermind_processing_queue_size
  Number of blocks awaiting for processing.


 nethermind_recovery_queue_size
  Number of blocks awaiting for recovery of public keys from signatures.


 nethermind_reorganizations
  Total number of chain reorganizations


 nethermind_sync_peers
  Number of sync peers.


 nethermind_transactions
  Total number of transactions processed


Evm
^^^


 nethermind_blockhash_opcode
  Number of BLOCKHASH opcodes executed.


 nethermind_bn128add_precompile
  Number of BN128_ADD precompile calls.


 nethermind_bn128mul_precompile
  Number of BN128_MUL precompile calls.


 nethermind_bn128pairing_precompile
  Number of BN128_PAIRING precompile calls.


 nethermind_calls
  Number of calls to other contracts.


 nethermind_ec_recover_precompile
  Number of EC_RECOVERY precompile calls.


 nethermind_evm_exceptions
  Number of EVM exceptions thrown by contracts.


 nethermind_mod_exp_opcode
  Number of MODEXP precompiles executed.


 nethermind_mod_exp_precompile
  Number of MODEXP precompile calls.


 nethermind_ripemd160precompile
  Number of RIPEMD160 precompile calls.


 nethermind_self_destructs
  Number of SELFDESTRUCT calls.


 nethermind_sha256precompile
  Number of SHA256 precompile calls.


 nethermind_sload_opcode
  Number of SLOAD opcodes executed.


 nethermind_sstore_opcode
  Number of SSTORE opcodes executed.


JsonRpc
^^^^^^^


 nethermind_json_rpc_errors
  Number of JSON RPC requests processed with errors.


 nethermind_json_rpc_invalid_requests
  Number of JSON RPC requests that were invalid.


 nethermind_json_rpc_request_deserialization_failures
  Number of JSON RPC requests that failed JSON deserialization.


 nethermind_json_rpc_requests
  Total number of JSON RPC requests received by the node.


 nethermind_json_rpc_successes
  Number of JSON RPC requests processed succesfully.


Network
^^^^^^^


 nethermind_already_connected_disconnects
  Number of received disconnects due to already connected


 nethermind_breach_of_protocol_disconnects
  Number of received disconnects due to breach of protocol


 nethermind_client_quitting_disconnects
  Number of received disconnects due to client quitting


 nethermind_disconnect_requested_disconnects
  Number of received disconnects due to disconnect requested


 nethermind_eth62block_bodies_received
  Number of eth.62 BlockBodies messages received


 nethermind_eth62block_headers_received
  Number of eth.62 BlockHeaders messages received


 nethermind_eth62get_block_bodies_received
  Number of eth.62 GetBlockBodies messages received


 nethermind_eth62get_block_headers_received
  Number of eth.62 GetBlockHeaders messages received


 nethermind_eth62new_block_hashes_received
  Number of eth.62 NewBlockHashes messages received


 nethermind_eth62new_block_received
  Number of eth.62 NewBlock messages received


 nethermind_eth62transactions_received
  Number of eth.62 Transactions messages received


 nethermind_eth63get_node_data_received
  Number of eth.63 GetNodeData messages received


 nethermind_eth63get_receipts_received
  Number of eth.63 GetReceipts messages received


 nethermind_eth63node_data_received
  Number of eth.63 NodeData messages received


 nethermind_eth63receipts_received
  Number of eth.63 Receipts messages received


 nethermind_eth65get_pooled_transactions_received
  Number of eth.65 GetPooledTransactions messages received


 nethermind_eth65new_pooled_transaction_hashes_received
  Number of eth.65 NewPooledTransactionHashes messages received


 nethermind_handshakes
  Number of devp2p handshakes


 nethermind_handshake_timeouts
  Number of devp2p handshke timeouts


 nethermind_hellos_received
  Number of devp2p hello messages received


 nethermind_hellos_sent
  Number of devp2p hello messages sent


 nethermind_incoming_connections
  Number of incoming connection.


 nethermind_incompatible_p2pdisconnects
  Number of received disconnects due to incompatible devp2p version


 nethermind_les_statuses_received
  Number of les status messages received


 nethermind_les_statuses_sent
  Number of les status messages sent


 nethermind_local_already_connected_disconnects
  Number of initiated disconnects due to already connected


 nethermind_local_breach_of_protocol_disconnects
  Number of sent disconnects due to breach of protocol


 nethermind_local_client_quitting_disconnects
  Number of initiated disconnects due to client quitting


 nethermind_local_disconnect_requested_disconnects
  Number of initiated disconnects due to disconnect requested


 nethermind_local_incompatible_p2pdisconnects
  Number of initiated disconnects due to incompatible devp2p


 nethermind_local_null_node_identity_disconnects
  Number of initiated disconnects due to missing node identity


 nethermind_local_other_disconnects
  Number of initiated disconnects due to other reason


 nethermind_local_receive_message_timeout_disconnects
  Number of initiated disconnects due to request timeout


 nethermind_local_same_as_self_disconnects
  Number of initiated disconnects due to connection to self


 nethermind_local_tcp_subsystem_error_disconnects
  Number of initiated disconnects due to TCP error


 nethermind_local_too_many_peers_disconnects
  Number of initiated disconnects due to breach of protocol


 nethermind_local_unexpected_identity_disconnects
  Number of initiated disconnects due to node identity info mismatch


 nethermind_local_useless_peer_disconnects
  Number of sent disconnects due to useless peer


 nethermind_null_node_identity_disconnects
  Number of received disconnects due to missing peer identity


 nethermind_other_disconnects
  Number of received disconnects due to other reasons


 nethermind_outgoing_connections
  Number of outgoing connection.


 nethermind_receive_message_timeout_disconnects
  Number of received disconnects due to request timeouts


 nethermind_same_as_self_disconnects
  Number of received disconnects due to connecting to self


 nethermind_statuses_received
  Number of eth status messages received


 nethermind_statuses_sent
  Number of eth status messages sent


 nethermind_tcp_subsystem_error_disconnects
  Number of disconnects due to TCP error


 nethermind_too_many_peers_disconnects
  Number of received disconnects due to too many peers


 nethermind_unexpected_identity_disconnects
  Number of received disconnects due to peer identity information mismatch


 nethermind_useless_peer_disconnects
  Number of received disconnects due to useless peer

