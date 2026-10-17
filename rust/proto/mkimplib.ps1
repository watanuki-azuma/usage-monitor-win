# Builds a MinGW import library (lib<name>.a) from C:\Windows\System32\<name>.dll exports,
# for system DLLs that the rustup windows-gnu self-contained toolchain does not ship.
param([Parameter(Mandatory = $true)][string[]]$Names)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin = "$env:USERPROFILE\.rustup\toolchains\stable-x86_64-pc-windows-gnu\lib\rustlib\x86_64-pc-windows-gnu\bin"
$lib = Join-Path $here 'lib'
New-Item -ItemType Directory -Force $lib | Out-Null
foreach ($n in $Names) {
    $dll = "C:\Windows\System32\$n.dll"
    $exports = & "$bin\llvm-readobj.exe" --coff-exports $dll |
        Select-String '^\s+Name: (\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value }
    $def = Join-Path $lib "$n.def"
    Set-Content $def -Encoding ascii -Value (@("LIBRARY $n.dll", 'EXPORTS') + $exports)
    & (Join-Path $here 'llvm-dlltool.exe') -d $def -D "$n.dll" -l (Join-Path $lib "lib$n.a") -m i386:x86-64
    "lib$n.a: $($exports.Count) exports"
}
