# Build status — verified record

Final image: `ghcr.io/source2destination/act2-agent:v3`
Digest: `sha256:ff72e50fa8349cfb26f84147c70084ac0c4c1f323b41e077601ab9a4c6b57245`
Frozen: 2026-07-12, ahead of the Track 1 deadline. No further pushes.

## Gates cleared by this exact source tree

| Gate | Result |
|---|---|
| `dotnet publish` (Release) | clean, 0 warnings / 0 errors |
| Self-test suite | 22/22 |
| Organizer retired validation set | 10/10 (content-graded against published criteria) |
| Generated regression pool (seed 909, 64 tasks) | 64/64 |
| Fresh-seed pools (1337 + 2607, 160 tasks each, seeds unseen during tuning) | 320/320 |
| Dead-proxy degradation (remote unreachable) | 64/64 task IDs, valid JSON, zero blanks, 103s |
| Registry pull-back (exact GHCR digest, 2 vCPU / 4 GB) | retired set 10/10 |

Static source audit: `artifacts/static-audit.log`.
File integrity: `FILE-MANIFEST.sha256` (SHA-256 over every tracked file).

## Reproduce

`tests/Run-PeakGate.ps1` runs build → self-test → smoke against a local image tag.
The retired-set pull-back in the table is the Quickstart command in [`README.md`](README.md) with the retired tasks as input.
