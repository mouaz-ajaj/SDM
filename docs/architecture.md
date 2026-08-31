# Architecture

## Structure and responsibilities

- **SDM.Core** contains the smallest domain surface: download state, a validated download request, and the future download-engine contract. It references no other SDM project.
- **SDM.Application** contains application-level services. In Stage 1 it exposes product metadata through `IApplicationInfoService`. It references only SDM.Core.
- **SDM.Infrastructure** is the future home of HTTP, filesystem, operating-system, notification, and external-process implementations. Its Stage 1 registration method intentionally registers no speculative implementation.
- **SDM.Database** is the future home of SQLite persistence. Stage 1 establishes only its project and registration boundary; there is no schema, migration, or repository implementation.
- **SDM.Desktop** contains Avalonia views and ViewModels and acts as the composition root. It can reference every lower layer; no project may reference it.

## Dependency direction

```text
SDM.Core
    ↑
SDM.Application
    ↑             ↑
SDM.Infrastructure  SDM.Database
          \          /
           SDM.Desktop
```

The product projects are also checked by a deterministic test that inspects their project references. Circular references and references to SDM.Desktop from lower layers are rejected.

## Composition root

`SdmBootstrapper` loads `appsettings.json`, configures Microsoft logging, invokes `AddSdmApplication()`, `AddSdmInfrastructure()`, `AddSdmDatabase()`, and `AddSdmDesktop()`, then builds a validated service provider. `Program` owns the provider for the application lifetime and records startup, version, dependency initialization, shutdown, and unexpected startup failures.

`App` resolves `MainWindow` through dependency injection. `MainWindow` receives `MainWindowViewModel`; the ViewModel obtains product metadata from the application service. XAML code-behind contains only Avalonia initialization and DataContext assignment.

## Separation rationale

UI code must not stream HTTP responses, write download files, access SQLite, or call operating-system integrations directly. Keeping those responsibilities behind Core/Application contracts makes the download engine independently testable, prevents UI lifecycle concerns from leaking into transfers, and allows persistence or UI technology to change without rewriting domain behavior.

## Stage 2 extension point

Stage 2 should refine the minimal `IDownloadEngine` contract only as concrete requirements demand, then add a basic HTTP/HTTPS implementation in SDM.Infrastructure. That implementation should receive `HttpClient` through dependency injection, keep destination-file operations behind an abstraction where useful, avoid persistence initially, and be tested with a deterministic local HTTP server rather than the public internet. SDM.Desktop should call an Application use case, never the infrastructure implementation directly.
