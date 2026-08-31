# ADR-003: Defer browser integration

## Status

Accepted

## Context

Browser extensions and Native Messaging introduce security, packaging, compatibility, and lifecycle concerns. Building them before the application and download engine have stable contracts would create avoidable coupling and speculative interfaces.

## Decision

Plan browser integration for a later stage and exclude browser extension projects, Native Messaging Hosts, cookie transfer, and browser-specific contracts from Stage 1.

## Consequences

The foundational solution remains focused and testable. Users cannot yet send downloads from a browser. Later browser work must integrate through explicit application contracts without bypassing validation or calling infrastructure from the UI.
