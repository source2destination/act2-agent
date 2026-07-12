# Build status

## Completed

- Peak source changes applied to the public harness copy.
- New source files are explicitly included in the public `.csproj` disclosure boundary.
- Both Docker build COPY lists include every new source file.
- `zero/Dockerfile.zero` enables deterministic/local-first Peak routing.
- Python AST validation is available in the zero image through `python3-minimal`.
- Static source audit passes.
- A PowerShell build/self-test/smoke script is included.

## Environment limitation

The current artifact runtime does not contain the .NET SDK or Docker CLI, so compilation and container execution could not be performed here. No claim is made that the C# build or Docker smoke gate passed in this environment.

Run `tests/Run-PeakGate.ps1` on the actual Windows repo to produce the authoritative build and smoke result.
