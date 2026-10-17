# Rust + Slint rewrite — notes

## R0 (2026-10-18)

- `build.ps1`: RESULT 0 passed, 0 failed; APP `target\release\UsageMonitor.exe` 8,157,696 bytes
  (placeholder window only).
- Toolchain findings (also in SLINT-SPEC.md §2): no `slint-build` (build script crashes), tests in
  `--release` (debug binaries linking Slint crash at start-up).
- `cargo tree`: no `cc` / `ring` / `aws-lc-sys` / `openssl-sys`; TLS = `ureq` → `native-tls` (SChannel).

## Open points

(none yet)
