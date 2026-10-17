# Builds and tests the Rust + Slint app with the rustup x86_64-pc-windows-gnu toolchain only
# (no MSVC, no full MinGW). Works around two gaps of the self-contained GNU toolchain
# (SLINT-SPEC.md §2):
#   1. its dlltool needs as.exe, which is not shipped -> use llvm-ar renamed to llvm-dlltool.exe
#   2. some system import libraries are missing (e.g. shlwapi) -> generate lib<name>.a from System32
# Prints "RESULT: <passed> passed, <failed> failed" and "APP: <path> (<bytes> bytes)".
param(
    [switch]$SkipTests,
    [switch]$SkipRelease
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$tools = Join-Path $root '.tools'
$libDir = Join-Path $tools 'lib'

$cargoBin = Join-Path $env:USERPROFILE '.cargo\bin'
if (($env:PATH -split ';') -notcontains $cargoBin) { $env:PATH = "$cargoBin;$env:PATH" }
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { throw "cargo not found (expected in $cargoBin)" }

$sysroot = (& rustc --print sysroot).Trim()
$rustBin = Join-Path $sysroot 'lib\rustlib\x86_64-pc-windows-gnu\bin'
if (-not (Test-Path (Join-Path $rustBin 'llvm-ar.exe'))) {
    & cmd /c "rustup component add llvm-tools 2>&1" | Out-Host
}
New-Item -ItemType Directory -Force $libDir | Out-Null
$dlltool = Join-Path $tools 'llvm-dlltool.exe'
foreach ($pair in @(@('llvm-ar.exe', 'llvm-dlltool.exe'), @('libgcc_s_seh-1.dll', 'libgcc_s_seh-1.dll'), @('libwinpthread-1.dll', 'libwinpthread-1.dll'))) {
    $src = Join-Path $rustBin $pair[0]
    $dst = Join-Path $tools $pair[1]
    if ((Test-Path $src) -and (-not (Test-Path $dst) -or (Get-Item $src).LastWriteTimeUtc -gt (Get-Item $dst).LastWriteTimeUtc)) {
        Copy-Item $src $dst -Force
    }
}

function New-ImportLib([string]$name) {
    $dll = "C:\Windows\System32\$name.dll"
    if (-not (Test-Path $dll)) { throw "cannot create lib$name.a: $dll not found" }
    $exports = & (Join-Path $rustBin 'llvm-readobj.exe') --coff-exports $dll |
        Select-String '^\s+Name: (\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value }
    $def = Join-Path $libDir "$name.def"
    [IO.File]::WriteAllLines($def, [string[]](@("LIBRARY $name.dll", 'EXPORTS') + $exports))
    & $dlltool -d $def -D "$name.dll" -l (Join-Path $libDir "lib$name.a") -m i386:x86-64
    if ($LASTEXITCODE -ne 0) { throw "llvm-dlltool failed for $name" }
    Write-Host "generated lib$name.a ($($exports.Count) exports)"
}
foreach ($name in @('shlwapi')) {
    if (-not (Test-Path (Join-Path $libDir "lib$name.a"))) { New-ImportLib $name }
}

# CARGO_ENCODED_RUSTFLAGS (0x1F-separated) keeps paths with spaces intact
$sep = [char]0x1F
$env:CARGO_ENCODED_RUSTFLAGS = (@('-C', "dlltool=$dlltool", '-L', "native=$libDir") -join $sep)
Remove-Item Env:RUSTFLAGS -ErrorAction SilentlyContinue

# Runs cargo; on "cannot find -l<name>" generates the import library and retries.
function Invoke-Cargo([string]$cargoArgs) {
    for ($i = 0; $i -lt 6; $i++) {
        Push-Location $root
        try { $out = & cmd /c "cargo $cargoArgs --color never 2>&1" } finally { Pop-Location }
        $code = $LASTEXITCODE
        $missing = @($out | Select-String 'cannot find -l(\w+)' -AllMatches |
            ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        if ($code -eq 0 -or $missing.Count -eq 0) { return @{ Code = $code; Out = $out } }
        foreach ($m in $missing) { New-ImportLib $m }
    }
    return @{ Code = $code; Out = $out }
}

$passed = 0; $failed = 0; $ok = $true
if (-not $SkipTests) {
    # Tests run with the release profile: non-LTO (debug) binaries that link Slint crash at start-up
    # with STATUS_ACCESS_VIOLATION under this windows-gnu toolchain (cause not found yet).
    $r = Invoke-Cargo 'test --release'
    $r.Out | Where-Object { $_ -notmatch '^\s+(Compiling|Running|Doc-tests|Finished)' -and $_ -notmatch '^test .* \.\.\. ok$' } | Out-Host
    foreach ($m in ($r.Out | Select-String 'test result: \w+\. (\d+) passed; (\d+) failed')) {
        $passed += [int]$m.Matches[0].Groups[1].Value
        $failed += [int]$m.Matches[0].Groups[2].Value
    }
    if ($r.Code -ne 0 -and $failed -eq 0) { $failed = 1; Write-Host 'cargo test failed (build error)' }
    if ($r.Code -ne 0) { $ok = $false }
}
Write-Host "RESULT: $passed passed, $failed failed"

if (-not $SkipRelease -and $ok) {
    $r = Invoke-Cargo 'build --release'
    $r.Out | Where-Object { $_ -notmatch '^\s+Compiling' } | Out-Host
    $exe = Join-Path $root 'target\release\UsageMonitor.exe'
    if ($r.Code -eq 0 -and (Test-Path $exe)) {
        Write-Host ("APP: {0} ({1:N0} bytes)" -f $exe, (Get-Item $exe).Length)
    } else {
        $ok = $false
        Write-Host 'release build failed'
    }
}
if (-not $ok) { exit 1 }
