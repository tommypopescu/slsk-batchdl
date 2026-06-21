# Sockseek 3.0-dev14 Manual Test Fix Tracking

## In Progress

- None.

## Fixed In Working Tree

- SS3-004: final file placement failures now fail the candidate/job instead of reporting success; focused song and album regression tests added.
- SS3-018: daemon listen IP is validated before Kestrel host construction; invalid IPs are rejected and IPv6 URLs are bracketed correctly.
- SS3-001: Linux/macOS tar.gz packaging now writes explicit Unix file modes; `sockseek` is archived as executable (`0755`).
- SS3-003: name-formatted downloads now use per-job staging paths and publish duplicate-cache entries only after final organization; concurrent duplicate rows with unique `{snum}` outputs are covered by regression test.

## Still Pending

- SS3-002: daemon headless/redirected startup hang.
- SS3-005: exit code contract.

## Checked But Not Yet Closed

- SS3-002: redirected daemon startup did not reproduce locally with `dotnet run --no-build` or the built debug `sockseek.exe`; both opened `/api/server/info` normally under redirected stdout/stderr. Keep pending until the original release-binary/headless environment is rechecked.

