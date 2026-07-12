# Contributing to PCBHelper

PCBHelper welcomes focused contributions that make small, simple PCB workflows safer, more deterministic, or easier to test.

## Before opening a change

1. Open or reference an issue for behavioral changes.
2. Keep KiCad mutations reversible and project-scoped.
3. Reuse the Design Plan transaction boundary instead of adding raw file or shell access.
4. Add unit and contract coverage proportional to the change.
5. Do not commit private boards, generated manufacturing outputs, credentials, or absolute personal paths.

## Local validation

```powershell
dotnet restore PCBHelper.slnx
dotnet build PCBHelper.slnx --configuration Release --no-restore
dotnet test tests/PCBHelper.Core.Tests/PCBHelper.Core.Tests.csproj --configuration Release --no-build
dotnet test tests/PCBHelper.Contract.Tests/PCBHelper.Contract.Tests.csproj --configuration Release --no-build
./scripts/Test-PublicTree.ps1
```

Docker clean-room tests are available through `./scripts/Test-DockerCleanRoom.ps1`. KiCad-dependent tests require KiCad 10; simulation tests require ngspice.

By contributing, you agree that your contribution is licensed under Apache-2.0.
