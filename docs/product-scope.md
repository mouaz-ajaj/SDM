# Product scope

## Direction

SDM (Speed Download Manager) is a modern, fast, technical, and reliable desktop download manager. The first release is Windows-first. Code should remain portable when portability is inexpensive and does not compromise the initial Windows experience.

Later stages are expected to add direct HTTP/HTTPS downloads, pause and resume, queues, browser integration, direct media handling, HLS, and DASH. Those capabilities are not part of Stage 1.

## Product boundaries

- Browser integration is planned for a later stage; Stage 1 has no extension or Native Messaging Host.
- BitTorrent is not included in version 1.
- Cloud downloading and a cloud backend are out of scope.
- Mobile applications are out of scope.
- DRM bypass is prohibited.
- SDM does not promise support for every video website.
- Website-specific media support must respect site terms, authorization, and applicable law.
- Installers, updates, telemetry, and a finished production interface are deferred.

## Stage 1 outcome

Stage 1 establishes a testable solution, dependency boundaries, desktop composition root, minimal branded shell, automated tests, and Windows CI. It deliberately provides no implementation that pretends downloads or persistence work.
