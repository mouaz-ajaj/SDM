# ADR-002: Windows-first delivery

## Status

Accepted

## Context

The first SDM release targets Windows, where the primary browser and desktop integration work will be tested. Future Linux and macOS support is desirable, but committing to equal platform support now would expand testing and integration scope before the download engine exists.

## Decision

Treat Windows as the first supported and required CI system. Prefer portable .NET and Avalonia APIs whenever doing so is inexpensive, and keep future operating-system behavior behind infrastructure abstractions.

## Consequences

Windows behavior receives the strongest validation and GitHub Actions runs on `windows-latest`. Portable architecture lowers later migration cost, but Linux and macOS are not supported or guaranteed during the foundational stages.
