# SMB Manager 2.0.5 client source

This tree contains the reviewed 2.0.5 patch sources based on build37. It includes the published 1.7.12 legacy hash catalog and the single-instance activation fix. It excludes the separate asset-survey integration work. SOURCE-MANIFEST.json records current patch inputs, not the original build37 bytes or runtime QA approval.

Build on Windows with .NET 10 SDK:

    dotnet publish ClientDesktop/ClientDesktop.csproj -c Release -r win-x64 --self-contained false -p:FileVersion=2.0.145.0 -p:InformationalVersion=2.0.5+patch.r3

Current servers can expose explicit `stable` / `beta` choices through `/api/client/updates`. A 404 on channel discovery falls back to the legacy endpoints; authentication and approval failures never fall back. Legacy catalogs stay labeled beta, preserve the saved allowed key, and do not become stable merely because their display text changes. Full stable/beta availability requires the matching server update and separately verified catalogs.

The patch preserves MSI version 2.0.145 and file version 2.0.145.0. Its MSI permits same-version replacement inside the rollback transaction. The original installed updater compares file versions and cannot automatically discover this same-version patch; use the uniquely named MSI for an explicit installation until a separately verified revision-aware delivery protocol is available. A machine with the unpublished 2.0.146 package is outside this same-version upgrade path; do not silently downgrade it.

The final MSI also bundles a Microsoft runtime, installer support helpers and company signatures. A local unsigned rebuild is not byte-identical to the signed release. The release assets identify the exact installer SHA256 and QA scope. Historical 1.x source and tags remain unchanged.
