//! leanXMSS and leanSPHINCS against the test vectors of EthLib, the Lean specification of both schemes.
//!
//! `ethlib/xmss/` and `ethlib/sphincs/` are EthLib's `tests/cryptography/xmss/` and
//! `tests/cryptography/sphincs/`, vendored by `ethlib/sync.sh`.
//! `ethlib/COMMIT` names the one EthLib commit both come from.
//!
//! Every case runs, and each must give EthLib's output byte for byte:
//!
//! - `key_gen`: the public key of a seed.
//! - `sign`: the signature of a message, at a leaf index for leanXMSS.
//! - `verify`: whether a signature verifies.
//! - `signature_index`, leanXMSS only: the leaf index a signature names.
//!
//! An output of `null` marks an input EthLib rejects.
//! Here such an input has the wrong length, so it does not fit the API's fixed-size arrays.

use std::collections::{BTreeMap, HashMap};
use std::fs;
use std::path::{Path, PathBuf};

/// One value of a case, as EthLib writes it.
#[derive(Debug, PartialEq)]
enum Value {
    /// `'0x…'`: a byte string.
    Bytes(Vec<u8>),
    /// A decimal integer.
    Int(u64),
    /// `true` or `false`.
    Bool(bool),
    /// `null`: the input is rejected.
    Null,
}

/// One case: its name, its named inputs and its output.
struct Case {
    name: String,
    input: BTreeMap<String, Value>,
    output: Value,
}

impl Case {
    /// The byte string input `key`.
    fn bytes(&self, key: &str) -> &[u8] {
        match self.input.get(key) {
            Some(Value::Bytes(bytes)) => bytes,
            other => panic!("{}: input {key} is {other:?}, not bytes", self.name),
        }
    }

    /// The integer input `key`.
    fn int(&self, key: &str) -> u64 {
        match self.input.get(key) {
            Some(Value::Int(n)) => *n,
            other => panic!("{}: input {key} is {other:?}, not an integer", self.name),
        }
    }
}

/// Decode `0x`-prefixed hex.
fn hex(s: &str) -> Vec<u8> {
    let digits = s.strip_prefix("0x").expect("hex starts with 0x");
    assert!(digits.len().is_multiple_of(2), "odd hex length");
    (0..digits.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&digits[i..i + 2], 16).expect("hex digit"))
        .collect()
}

/// One scalar, in the subset of YAML EthLib's cases use.
fn value(s: &str) -> Value {
    match s {
        "null" => Value::Null,
        "true" => Value::Bool(true),
        "false" => Value::Bool(false),
        _ => match s.strip_prefix('\'').and_then(|s| s.strip_suffix('\'')) {
            Some(quoted) => Value::Bytes(hex(quoted)),
            None => Value::Int(s.parse().unwrap_or_else(|_| panic!("unknown value {s}"))),
        },
    }
}

/// Read one `data.yaml`, rejecting any line outside its fixed shape:
///
/// ```text
/// input:
///   <key>: <value>
///   ...
/// output: <value>
/// ```
fn parse(name: String, text: &str) -> Case {
    let mut lines = text.lines();
    assert_eq!(lines.next(), Some("input:"), "{name}: no input");
    let mut input = BTreeMap::new();
    for line in lines.by_ref() {
        // The indented lines are the inputs, and the first unindented one the output.
        let Some(field) = line.strip_prefix("  ") else {
            let output = line
                .strip_prefix("output: ")
                .unwrap_or_else(|| panic!("{name}: bad line {line}"));
            assert!(lines.next().is_none(), "{name}: text after the output");
            return Case {
                name,
                input,
                output: value(output),
            };
        };
        let (key, raw) = field
            .split_once(": ")
            .unwrap_or_else(|| panic!("{name}: bad input {field}"));
        input.insert(key.to_owned(), value(raw));
    }
    panic!("{name}: no output")
}

/// Every case of one handler of one scheme, in name order.
fn cases(scheme: &str, handler: &str) -> Vec<Case> {
    let dir: PathBuf = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests/signatures/ethlib")
        .join(scheme)
        .join(handler);
    let mut names: Vec<String> = fs::read_dir(&dir)
        .unwrap_or_else(|e| panic!("{}: {e}", dir.display()))
        .map(|entry| entry.unwrap())
        .filter(|entry| entry.file_type().unwrap().is_dir())
        .map(|entry| entry.file_name().into_string().unwrap())
        .collect();
    names.sort();
    assert!(!names.is_empty(), "no {scheme} {handler} cases");
    names
        .into_iter()
        .map(|name| {
            let text = fs::read_to_string(dir.join(&name).join("data.yaml")).unwrap();
            parse(name, &text)
        })
        .collect()
}

/// The fixed-size array a byte string must fill, or `None` for any other length.
fn exact<const L: usize>(bytes: &[u8]) -> Option<[u8; L]> {
    bytes.try_into().ok()
}

mod xmss {
    use super::*;
    use signatures::lean_xmss::*;

    #[test]
    fn key_gen_matches_ethlib() {
        for case in cases("xmss", "key_gen") {
            let output = match exact::<SEED_LEN>(case.bytes("seed")) {
                Some(seed) => Value::Bytes(key_gen_from_seed(seed).1.to_bytes().to_vec()),
                None => Value::Null,
            };
            assert_eq!(output, case.output, "{}", case.name);
        }
    }

    #[test]
    fn sign_matches_ethlib() {
        // A key is generated once per seed: generation builds the whole top tree.
        let mut keys: HashMap<Seed, LeanXmssSecretKey> = HashMap::new();
        for case in cases("xmss", "sign") {
            let leaf_index = LeafIndex::try_from(case.int("leaf_index")).expect("a 32-bit leaf index");
            let output = match (
                exact::<SEED_LEN>(case.bytes("seed")),
                exact::<MESSAGE_LEN>(case.bytes("message")),
            ) {
                (Some(seed), Some(message)) => {
                    let sk = keys.entry(seed).or_insert_with(|| key_gen_from_seed(seed).0);
                    Value::Bytes(sign(sk, &message, leaf_index).unwrap().to_bytes().to_vec())
                }
                _ => Value::Null,
            };
            assert_eq!(output, case.output, "{}", case.name);
        }
    }

    #[test]
    fn signature_index_matches_ethlib() {
        for case in cases("xmss", "signature_index") {
            let output = match exact::<SIG_SIZE>(case.bytes("signature")) {
                Some(sig) => Value::Int(u64::from(LeanXmssSignature::from_bytes(&sig).leaf_index())),
                None => Value::Null,
            };
            assert_eq!(output, case.output, "{}", case.name);
        }
    }

    #[test]
    fn verify_matches_ethlib() {
        for case in cases("xmss", "verify") {
            let output = match (
                exact::<PUB_KEY_SIZE>(case.bytes("public_key")),
                exact::<MESSAGE_LEN>(case.bytes("message")),
                exact::<SIG_SIZE>(case.bytes("signature")),
            ) {
                (Some(pk), Some(message), Some(sig)) => Value::Bool(
                    verify(
                        &LeanXmssPublicKey::from_bytes(&pk),
                        &message,
                        &LeanXmssSignature::from_bytes(&sig),
                    )
                    .is_ok(),
                ),
                _ => Value::Null,
            };
            assert_eq!(output, case.output, "{}", case.name);
        }
    }
}

mod sphincs {
    use super::*;
    use signatures::lean_sphincs::*;

    #[test]
    fn key_gen_matches_ethlib() {
        for case in cases("sphincs", "key_gen") {
            let output = match exact::<SEED_LEN>(case.bytes("seed")) {
                Some(seed) => Value::Bytes(key_gen_from_seed(seed).1.to_bytes().to_vec()),
                None => Value::Null,
            };
            assert_eq!(output, case.output, "{}", case.name);
        }
    }

    #[test]
    fn sign_matches_ethlib() {
        for case in cases("sphincs", "sign") {
            let output = match (
                exact::<SEED_LEN>(case.bytes("seed")),
                exact::<MESSAGE_LEN>(case.bytes("message")),
            ) {
                (Some(seed), Some(message)) => Value::Bytes(
                    sign(&LeanSphincsSecretKey::from_bytes(&seed), &message)
                        .unwrap()
                        .to_bytes()
                        .to_vec(),
                ),
                _ => Value::Null,
            };
            assert_eq!(output, case.output, "{}", case.name);
        }
    }

    #[test]
    fn verify_matches_ethlib() {
        for case in cases("sphincs", "verify") {
            let output = match (
                exact::<PUB_KEY_SIZE>(case.bytes("public_key")),
                exact::<MESSAGE_LEN>(case.bytes("message")),
                exact::<SIG_SIZE>(case.bytes("signature")),
            ) {
                (Some(pk), Some(message), Some(sig)) => Value::Bool(
                    verify(
                        &LeanSphincsPublicKey::from_bytes(&pk),
                        &message,
                        &LeanSphincsSignature::from_bytes(&sig),
                    )
                    .is_ok(),
                ),
                _ => Value::Null,
            };
            assert_eq!(output, case.output, "{}", case.name);
        }
    }
}
