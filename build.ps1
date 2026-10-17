# Builds the native Usage Monitor and runs the tests up to the given milestone.
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Milestone 2
param(
  [int]$Milestone = 1,
  [string]$Filter = '',
  [switch]$NoTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'out'
New-Item -ItemType Directory -Force $out | Out-Null

$src = @(Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs | ForEach-Object { $_.FullName })
if ($src.Count -eq 0) { Write-Output 'RESULT: 0 passed, 1 failed (no source files)'; exit 1 }

$refs = @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll') | ForEach-Object { "/r:$_" }
$common = @('/nologo', '/langversion:5', '/codepage:65001', '/utf8output', '/warn:3', '/debug-', '/optimize+') + $refs

if (-not $NoTests) {
  $tests = @(Join-Path $root 'tests\Harness.cs')
  for ($m = 1; $m -le $Milestone; $m++) {
    $tests += @(Get-ChildItem (Join-Path $root 'tests') -Filter "M${m}_*.cs" | ForEach-Object { $_.FullName })
  }
  $testExe = Join-Path $out "tests-M$Milestone.exe"
  if (Test-Path $testExe) { Remove-Item $testExe -Force }
  $args1 = $common + @('/target:exe', "/out:$testExe", '/main:UsageMonitor.Tests.Runner') + $src + $tests
  $log = & $csc @args1 2>&1
  $log | ForEach-Object { Write-Output $_ }
  if (-not (Test-Path $testExe)) { Write-Output 'RESULT: 0 passed, 1 failed (test build failed)'; exit 1 }
  $runArgs = @()
  if ($Filter) { $runArgs += $Filter }
  # tests must never touch the real home directory.
  # Start-Process on PowerShell 5.1 refuses an empty -ArgumentList and leaves ExitCode null
  # when standard streams are redirected, so the process is started directly instead.
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $testExe
  $psi.UseShellExecute = $false
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.Arguments = ($runArgs -join ' ')
  $p = [System.Diagnostics.Process]::Start($psi)
  $stdOut = $p.StandardOutput.ReadToEnd()
  $stdErr = $p.StandardError.ReadToEnd()
  if (-not $p.WaitForExit(180000)) { $p.Kill(); Write-Output 'RESULT: 0 passed, 1 failed (tests timed out after 180s)'; exit 1 }
  Set-Content -Path (Join-Path $out 'test-out.txt') -Value $stdOut
  Set-Content -Path (Join-Path $out 'test-err.txt') -Value $stdErr
  Get-Content (Join-Path $out 'test-out.txt') | ForEach-Object { Write-Output $_ }
  Get-Content (Join-Path $out 'test-err.txt') | ForEach-Object { Write-Output "stderr: $_" }
  if ($p.ExitCode -ne 0) { exit 1 }
}

if ($Milestone -ge 5) {
  $exe = Join-Path $out 'UsageMonitor.exe'
  if (Test-Path $exe) { Remove-Item $exe -Force }
  $args2 = $common + @('/target:winexe', "/out:$exe") + $src
  $log = & $csc @args2 2>&1
  $log | ForEach-Object { Write-Output $_ }
  if (-not (Test-Path $exe)) { Write-Output 'RESULT: 0 passed, 1 failed (app build failed)'; exit 1 }
  Write-Output ("APP: {0} ({1:N0} bytes)" -f $exe, (Get-Item $exe).Length)
}
