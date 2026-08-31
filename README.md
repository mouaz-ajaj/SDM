# SDM — Speed Download Manager

SDM is a Windows-first desktop download manager. This repository currently contains **Stage 1: Project Foundation and Architecture**.

The application starts as a minimal Avalonia shell, loads configuration, builds its dependency graph, and logs its lifecycle. Actual downloading and browser integration are intentionally not implemented yet.

## Technology stack

- C# 14 and .NET 10
- Avalonia UI 12 with MVVM
- CommunityToolkit.Mvvm
- Microsoft dependency injection, configuration, and logging
- xUnit.net v3
- GitHub Actions on Windows

## Repository structure

```text
src/
  SDM.Core/            Domain models and download abstractions
  SDM.Application/     Application services and use-case orchestration
  SDM.Infrastructure/  Future networking, filesystem, and OS implementations
  SDM.Database/        Future persistence implementations
  SDM.Desktop/         Avalonia UI and composition root
tests/
  SDM.Core.Tests/
  SDM.Application.Tests/
  SDM.IntegrationTests/
docs/                  Product scope, architecture, and ADRs
.github/workflows/      Windows CI
```

The pre-existing planning document and generated PNG concepts remain one directory above this repository; they are not compiled as part of the solution.

## Prerequisites

- .NET SDK 10.0.301 or a compatible 10.0 feature band selected by `global.json`
- Windows 10 or later is the primary supported development environment for Stage 1

## Restore, build, and test

```powershell
dotnet restore SDM.sln
dotnet build SDM.sln --configuration Debug --no-restore
dotnet build SDM.sln --configuration Release --no-restore
dotnet test SDM.sln --configuration Release --no-build
```

## Run

```powershell
dotnet run --project src/SDM.Desktop/SDM.Desktop.csproj
```

The shell displays SDM product metadata and confirms that the foundation is initialized.

## Current limitations

Stage 1 does not perform network requests, create download files, calculate progress, persist jobs, or integrate with browsers. It has no queue, pause/resume support, media extraction, HLS/DASH handling, installer, updater, system tray, or finished production interface.

See [product scope](docs/product-scope.md), [architecture](docs/architecture.md), and the [architecture decision records](docs/decisions/) for the decisions that constrain later stages.
