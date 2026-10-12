param(
    [string] $OutputDirectory = $(if ($IsWindows) { 'C:/Temp/nethermind-lightclient-browser' } else { '/tmp/nethermind-lightclient-browser' })
)

$ErrorActionPreference = 'Stop'
$cacheDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $cacheDirectory | Out-Null

function Get-PinnedSource([string] $Name, [string] $Repository, [string] $Commit, [string] $Checksum) {
    $archive = Join-Path $cacheDirectory "$Name-$Commit.zip"
    if (!(Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest "https://codeload.github.com/$Repository/zip/$Commit" -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Checksum) {
        throw "The $Name source archive does not match its pinned SHA256 checksum."
    }
    $destination = Join-Path $cacheDirectory "$Name-$Commit"
    Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
    return (Get-ChildItem -LiteralPath $destination -Directory | Select-Object -First 1).FullName
}

$blst = Get-PinnedSource 'blst' 'supranational/blst' 'e7f90de551e8df682f3cc99067d204d8b90d27ad' '7217B5B90A71275E54580944258929573F60563D4C82CFB0F508805BE22870AC'
$bindings = Get-PinnedSource 'blst-bindings' 'NethermindEth/blst-bindings' '77442f26c7ee4bb46b3c12e7120c6325baa0f2e3' 'D1015A4D8E3C2C736501067037C13016DF515C4B6A83BA6CDFD2BD1BE675AC48'
$portable = Join-Path $cacheDirectory 'portable-bindings-1.1.0'
New-Item -ItemType Directory -Force -Path $portable | Out-Null
Get-ChildItem -LiteralPath (Join-Path $bindings 'src/Nethermind.Crypto.Bls') -Filter '*.cs' | Copy-Item -Destination $portable
$bindingPath = Join-Path $portable 'Bls.cs'
$bindingText = Get-Content -LiteralPath $bindingPath -Raw
$oldLoader = 'static Bls() => NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), LoadLibrary);'
if ([regex]::Matches($bindingText, [regex]::Escape($oldLoader)).Count -ne 1) {
    throw 'The pinned managed BLS loader no longer matches the browser patch.'
}
$bindingText.Replace($oldLoader, 'static Bls() { if (!OperatingSystem.IsBrowser()) NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), LoadLibrary); }') |
    Set-Content -LiteralPath $bindingPath
Copy-Item -LiteralPath (Join-Path $bindings 'LICENSE'), (Join-Path $bindings 'NOTICE') -Destination $portable

$secp = Get-PinnedSource 'secp256k1' 'bitcoin-core/secp256k1' '1a53f4961f337b4d166c25fce72ef0dc88806618' '56D3984742E584A0F8EA9409669754A892608DE866520FBC8671437F0AB2844E'
$secpBindings = Get-PinnedSource 'secp256k1-bindings' 'NethermindEth/secp256k1-bindings' 'cc56c2919db4f47fb7af865901225f8c53be5f03' '877534D3C57AD5FDC89AF1B4F1A57E66600C9552F6E07648B4CE7DA5A41D9D27'
$secpPortable = Join-Path $cacheDirectory 'portable-secp256k1-bindings-1.7.0'
New-Item -ItemType Directory -Force -Path $secpPortable | Out-Null
Get-ChildItem -LiteralPath (Join-Path $secpBindings 'src/Nethermind.Crypto.SecP256k1') -Filter '*.cs' | Copy-Item -Destination $secpPortable
$secpBindingPath = Join-Path $secpPortable 'SecP256k1.cs'
$secpBindingText = Get-Content -LiteralPath $secpBindingPath -Raw
$oldSecpLoader = 'AssemblyLoadContext.Default.ResolvingUnmanagedDll += OnResolvingUnmanagedDll;'
if ([regex]::Matches($secpBindingText, [regex]::Escape($oldSecpLoader)).Count -ne 1) {
    throw 'The pinned managed secp256k1 loader no longer matches the browser patch.'
}
$secpBindingText.Replace($oldSecpLoader, "if (!OperatingSystem.IsBrowser()) $oldSecpLoader") | Set-Content -LiteralPath $secpBindingPath
Copy-Item -LiteralPath (Join-Path $secpBindings 'LICENSE') -Destination $secpPortable

[pscustomobject]@{
    BlstSourceRoot = $blst
    BlstBindingsRoot = $portable
    SecpSourceRoot = $secp
    SecpBindingsRoot = $secpPortable
}
