param(
    [string] $OutputDirectory = $(if ($IsWindows) { 'C:/Temp/nethermind-lightclient-browser-precompiles' } else { '/tmp/nethermind-lightclient-browser-precompiles' })
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

function Prepare-Bindings([string] $Source, [string] $Name, [string] $LicenseRoot) {
    $destination = Join-Path $cacheDirectory "portable-$Name"
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    foreach ($file in (Get-ChildItem -LiteralPath $Source -Recurse -Filter '*.cs')) {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $target = Join-Path $destination $relative
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    foreach ($name in @('LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES')) {
        $license = Join-Path $LicenseRoot $name
        if (Test-Path -LiteralPath $license) { Copy-Item -LiteralPath $license -Destination $destination }
    }
    return $destination
}

function Replace-Exactly([string] $Path, [string] $Old, [string] $New) {
    $source = Get-Content -LiteralPath $Path -Raw
    if ([regex]::Matches($source, [regex]::Escape($Old)).Count -ne 1) {
        throw "The pinned source no longer matches the browser ABI patch: $Path"
    }
    $source.Replace($Old, $New) | Set-Content -LiteralPath $Path
}

$gmp = Get-PinnedSource 'gmp' 'gmp-mirror/gmp-6.3' 'fb2a20c31bab706024dce27878eed29a298fc69f' '340D6C849F679332A27499BCFAA2B777C7BB2BAC2A934A35C7807C407FD7E771'
$gmpBindingSource = Get-PinnedSource 'gmp-bindings' 'NethermindEth/gmp-bindings' '475841beeac3963f93321e2bbc72fa66478ac829' 'A2A5694B076576F6098DC7507781986DA0A3C11D5819418742A2760E71BF25B3'
$gmpBindings = Prepare-Bindings (Join-Path $gmpBindingSource 'src/Nethermind.GmpBindings') 'gmp-bindings-2.0.0' $gmpBindingSource
$gmpConstructor = @'
    static Gmp()
    {
'@
$gmpBrowserConstructor = @'
    static Gmp()
    {
        if (OperatingSystem.IsBrowser())
        {
            version = "6.3.0 mini-gmp";
            unsafe { mp_get_memory_functions(out _alloc, out _realloc, out _free); }
            return;
        }
'@
Replace-Exactly (Join-Path $gmpBindings 'Gmp.cs') $gmpConstructor $gmpBrowserConstructor
$miniGmp = Get-Content -LiteralPath (Join-Path $gmp 'mini-gmp/mini-gmp.h') -Raw
$macroSymbols = [regex]::Matches($miniGmp, '(?m)^#define\s+(mpz_[a-zA-Z0-9_]+)') |
    ForEach-Object { $_.Groups[1].Value }
$symbols = [regex]::Matches($miniGmp, '\b(mpz_[a-zA-Z0-9_]+|mp_get_memory_functions|mp_set_memory_functions)\s*\(') |
    ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -notin $macroSymbols } | Sort-Object -Unique
$aliases = @('#pragma once')
foreach ($symbol in $symbols) {
    $native = if ($symbol.StartsWith('mpz_')) { '__gmpz_' + $symbol.Substring(4) } else { '__gmp_' + $symbol.Substring(3) }
    $aliases += "#define $symbol $native"
}
$aliases | Set-Content -LiteralPath (Join-Path $gmp 'mini-gmp/mini-gmp-aliases.h')

$mcl = Get-PinnedSource 'mcl' 'herumi/mcl' 'e8df0f01e939fda21583258ccfc2caa868e7557c' 'A9CC07BC09C721B9AB0802564390A19734AFD71F0B54A0308746DF03490DB526'
$mclBindingSource = Get-PinnedSource 'mcl-bindings' 'NethermindEth/mcl-bindings' '8d552771b2cea39cddbe2b18e19bb880a07ad305' '4CADF07389D1A9EF2F3ADC868DF437FDD345687924FCEB1C84ACB618C7BEBEE3'
$mclBindings = Prepare-Bindings (Join-Path $mclBindingSource 'src/Nethermind.MclBindings') 'mcl-bindings-2.0.0' $mclBindingSource
Replace-Exactly (Join-Path $mclBindings 'Mcl.cs') 'static Mcl() => AssemblyLoadContext.Default.ResolvingUnmanagedDll += OnResolvingUnmanagedDll;' 'static Mcl() { if (!OperatingSystem.IsBrowser()) AssemblyLoadContext.Default.ResolvingUnmanagedDll += OnResolvingUnmanagedDll; }'

$ckzg = Get-PinnedSource 'ckzg' 'ethereum/c-kzg-4844' 'e125905e5e01186e6ccb7a0ced4845bf7eddbcfe' '140F0CBF6A278E8D327066DD61508A3E62509129F5A324E668C855DB20E92FA3'
$ckzgBindings = Prepare-Bindings (Join-Path $ckzg 'bindings/csharp/Ckzg.Bindings') 'ckzg-bindings-2.1.8.1641' $ckzg
Replace-Exactly (Join-Path $ckzgBindings 'Ckzg.Bindings.cs') 'static Ckzg() => AssemblyLoadContext.Default.ResolvingUnmanagedDll += LoadNativeLibrary;' 'static Ckzg() { if (!OperatingSystem.IsBrowser()) AssemblyLoadContext.Default.ResolvingUnmanagedDll += LoadNativeLibrary; }'
Replace-Exactly (Join-Path $ckzgBindings 'Ckzg.Bindings.cs') 'InternalLoadTrustedSetup(string filename, UInt64 precompute)' 'InternalLoadTrustedSetup(string filename, nuint precompute)'
Replace-Exactly (Join-Path $ckzgBindings 'Ckzg.cs') 'InternalLoadTrustedSetup(filepath, (UInt64)precompute)' 'InternalLoadTrustedSetup(filepath, (nuint)precompute)'

$boring = Get-PinnedSource 'boringssl' 'google/boringssl' '8b43ff0f72137b31b23d482f0b2344186d797b03' 'FC79FDA7FD82E625E36BF0BD53C9F38F40FEFCD19B052C34BAEA36A1E7F6AA94'
$p256BindingSource = Get-PinnedSource 'secp256r1-bindings' 'NethermindEth/secp256r1-bindings' '48aa622f80f75b80331394517fbfa068699b1305' '919DF5E3A5460633AE06D0CE8C0345F3397AFEA1023FC704188A9534063B5F4C'
$p256Bindings = Prepare-Bindings (Join-Path $p256BindingSource 'src/Nethermind.Crypto.SecP256r1') 'secp256r1-bindings-1.1.0' $p256BindingSource
Replace-Exactly (Join-Path $p256Bindings 'BoringSsl.cs') 'static BoringSsl() => SetLibraryFallbackResolver();' 'static BoringSsl() { if (!OperatingSystem.IsBrowser()) SetLibraryFallbackResolver(); }'

$boringManifest = Join-Path $cacheDirectory 'boringssl-sources.props'
$boringSources = Get-Content -LiteralPath (Join-Path $boring 'gen/sources.cmake') -Raw
$items = @('<Project>', '  <ItemGroup>')
foreach ($group in @('BCM_SOURCES', 'CRYPTO_SOURCES')) {
    $match = [regex]::Match($boringSources, "(?s)set\(\s+$group\s+(.*?)\)")
    if (!$match.Success) { throw "Pinned BoringSSL source manifest is missing $group." }
    foreach ($file in ($match.Groups[1].Value.Trim() -split '\s+')) {
        if (!(Test-Path -LiteralPath (Join-Path $boring $file))) { throw "Pinned BoringSSL source is missing $file." }
        $items += "    <NativeFileReference Include=`"`$(BoringSourceRoot)/$file`" ScanForPInvokes=`"false`" />"
    }
}
$items += @('  </ItemGroup>', '</Project>')
$items | Set-Content -LiteralPath $boringManifest

[pscustomobject]@{
    GmpSourceRoot = $gmp
    GmpBindingsRoot = $gmpBindings
    MclSourceRoot = $mcl
    MclBindingsRoot = $mclBindings
    CkzgSourceRoot = $ckzg
    CkzgBindingsRoot = $ckzgBindings
    BoringSourceRoot = $boring
    BoringSourcesManifest = $boringManifest
    Secp256r1BindingsRoot = $p256Bindings
}
