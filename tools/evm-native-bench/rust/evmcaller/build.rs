fn main() {
    let dir = std::env::var("EVM_LIB_DIR").expect("set EVM_LIB_DIR to the AOT publish directory");
    println!("cargo:rustc-link-search=native={dir}");
    println!("cargo:rustc-link-arg=-Wl,-rpath,{dir}");
}
