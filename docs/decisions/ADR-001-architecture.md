# ADR-001: Layered modular architecture

## Status

Accepted

## Context

SDM needs a desktop UI now and a download engine, persistence, and operating-system integrations later. Those concerns have different lifecycles and testing requirements. Allowing UI or infrastructure dependencies into the domain would make the download behavior difficult to test and replace.

## Decision

Use a layered modular solution with separate Core, Application, Infrastructure, Database, and Desktop projects. Dependencies point inward: Core has no SDM dependency; Application references Core; Infrastructure and Database reference Core and Application; Desktop is the composition root and references the required lower layers.

## Consequences

Business and transfer contracts remain independent from Avalonia, SQLite, and operating-system APIs. Project-reference tests can enforce the boundary. The solution has more projects than a single executable, but each project has a clear reason to change.
