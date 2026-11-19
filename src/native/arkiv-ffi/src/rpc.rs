//! The `arkiv_*` read methods, answered at the store's head with `arkiv-reth-rpc`'s own reads, cursors and errors.
//!
//! This is `arkiv_reth_rpc::arkiv_module` without reth's provider: the host passes the block number of the store's
//! head, which is the only block GolemDB v1.0.0 can read at. Requests for any other block are refused with the
//! same `-32006` the reference node answers; the host serves the historical entity reads it archives itself
//! before calling here.

use alloy_eips::BlockNumberOrTag;
use alloy_primitives::B256;
use arkiv_interfaces::query::{AnnotKey, AnnotVal, BuiltIn, Query};
use arkiv_interfaces::store::{CommitId, Sort, Store};
use arkiv_reth_rpc::cursor;
use arkiv_reth_rpc::error::{block_unavailable, cursor_error, internal_error, invalid_params, query_error};
use arkiv_reth_rpc::store_reads;
use arkiv_reth_statemanager::HostStore;
use arkiv_rpc_types::entity::{Projection, entity_data_from};
use arkiv_rpc_types::method::{CountRequest, QueryOptions, QueryResponse};
use jsonrpsee_types::ErrorObjectOwned;
use serde::Serialize;
use serde::de::DeserializeOwned;
use serde_json::{Value, json};

const NOT_RETAINED: &str = "state for this block is not retained";

/// Answer `method` with `params` at the head, which is block `head`.
pub fn dispatch(store: &HostStore, method: &str, params: &Value, head: u64) -> Value {
    let result = match method {
        "arkiv_getEntity" => get_entity(store, params, head, true),
        "arkiv_debugEntityExists" => get_entity(store, params, head, false),
        "arkiv_query" => query(store, params, head),
        "arkiv_getEntityCount" => count(store, params, head),
        _ => Err(ErrorObjectOwned::owned(-32601, format!("the method {method} does not exist/is not available"), None::<()>)),
    };
    match result {
        Ok(value) => json!({ "result": value }),
        Err(error) => json!({ "error": error }),
    }
}

fn param<T: DeserializeOwned>(params: &Value, index: usize, what: &str) -> Result<Option<T>, ErrorObjectOwned> {
    match params.get(index) {
        None | Some(Value::Null) => Ok(None),
        Some(value) => serde_json::from_value(value.clone()).map(Some).map_err(|e| invalid_params(format!("invalid {what}: {e}"))),
    }
}

fn to_json<T: Serialize>(value: T) -> Result<Value, ErrorObjectOwned> {
    serde_json::to_value(value).map_err(|e| internal_error(format!("serialise: {e}")))
}

/// The commit for `block`, which must be the head (`head`).
fn resolve(store: &HostStore, block: Option<u64>, head: u64) -> Result<(u64, CommitId), ErrorObjectOwned> {
    match block {
        None => Ok((head, store.head())),
        Some(n) if n == head => Ok((head, store.head())),
        Some(n) if n > head => Err(block_unavailable(n, head, "ahead of the chain tip")),
        Some(n) => Err(block_unavailable(n, head, NOT_RETAINED)),
    }
}

/// `arkiv-reth-rpc`'s liveness filter: an entity is live at `block` while its expiry is after it.
fn live_at(query: Query, block: u64) -> Query {
    Query::And(vec![query, Query::Gt { key: AnnotKey::BuiltIn(BuiltIn::ExpiresAt), value: AnnotVal::U64(block) }])
}

fn get_entity(store: &HostStore, params: &Value, head: u64, data: bool) -> Result<Value, ErrorObjectOwned> {
    let key: B256 = param(params, 0, "params")?.ok_or_else(|| invalid_params("invalid params: missing entity key".into()))?;
    let block: Option<u64> = param(params, 1, "block param")?;
    let (number, at) = resolve(store, block, head)?;
    let entity = store_reads::entity(&**store, at, key.0).map_err(|e| internal_error(format!("get entity: {e}")))?;
    if !data {
        return to_json(entity.is_some());
    }
    to_json(entity.filter(|e| e.expires_at > number).map(|e| entity_data_from(e, &Projection::all())))
}

fn query(store: &HostStore, params: &Value, head: u64) -> Result<Value, ErrorObjectOwned> {
    let q: String = param(params, 0, "query param")?.ok_or_else(|| invalid_params("invalid query param: missing".into()))?;
    let options: QueryOptions = param(params, 1, "options param")?.unwrap_or_default();
    let statement = arkiv_query::parse_statement(&q).map_err(|e| query_error(&e))?;
    let sort = statement.order.as_ref().map(Sort::from);
    let projection = Projection::resolve(options.select.as_ref()).map_err(invalid_params)?;
    let page_size = arkiv_rpc_types::method::resolve_limit(options.limit).map_err(invalid_params)?;
    let at_block = match options.at_block {
        None | Some(BlockNumberOrTag::Latest) => None,
        Some(BlockNumberOrTag::Number(n)) => Some(n),
        Some(other) => {
            return Err(invalid_params(format!("atBlock tag {other:?} not supported; use a hex block number or 'latest'")));
        }
    };
    let (block_number, at) = resolve(store, at_block, head)?;
    let binding = cursor::binding(&q, block_number, &projection.fingerprint());
    let offset = match options.cursor.as_deref() {
        None => options.offset.unwrap_or(0),
        Some(text) => cursor::decode(text, binding).map_err(|e| cursor_error(e.message()))?,
    };
    let live = live_at(statement.filter.clone(), block_number);
    if projection.is_count_only() {
        let count = store_reads::count(&**store, at, &live).map_err(|e| internal_error(format!("count: {e}")))?;
        return to_json(QueryResponse { data: Vec::new(), block_number, cursor: None, count: Some(count) });
    }
    let page = store_reads::query(&**store, at, &live, sort.as_ref(), offset, page_size, projection.count)
        .map_err(|e| internal_error(format!("query: {e}")))?;
    let more = page.entities.len() as u64 == page_size;
    let data = page.entities.into_iter().map(|e| entity_data_from(e, &projection)).collect();
    to_json(QueryResponse {
        data,
        block_number,
        cursor: more.then(|| cursor::encode(offset + page_size, binding)),
        count: page.total_matched,
    })
}

fn count(store: &HostStore, params: &Value, head: u64) -> Result<Value, ErrorObjectOwned> {
    let request: CountRequest = param(params, 0, "params")?.unwrap_or_default();
    let query = match &request.query {
        Some(text) => arkiv_query::parse_statement(text).map_err(|e| query_error(&e))?.filter,
        None => Query::All,
    };
    let (block_number, at) = resolve(store, request.block, head)?;
    let count = store_reads::count(&**store, at, &live_at(query, block_number)).map_err(|e| internal_error(format!("count: {e}")))?;
    to_json(count)
}
