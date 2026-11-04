# SMB Manager 2.0.5 client source

This tree contains the reviewed 2.0.5 patch sources based on build37. It includes the published 1.7.12 legacy hash catalog and the single-instance activation fix. It excludes the separate asset-survey integration work. SOURCE-MANIFEST.json records current patch inputs, not the original build37 bytes or runtime QA approval.

Build on Windows with .NET 10 SDK:

    dotnet publish ClientDesktop/ClientDesktop.csproj -c Release -r win-x64 --self-contained false -p:FileVersion=2.0.145.1 -p:InformationalVersion=2.0.5+patch.r4

Current servers can expose explicit `stable` / `beta` choices through `/api/client/updates`. A 404 on channel discovery falls back to the legacy endpoints; authentication and approval failures never fall back. Legacy catalogs stay labeled beta, preserve the saved allowed key, and do not become stable merely because their display text changes. Full stable/beta availability requires the matching server update and separately verified catalogs.

The user approved increasing only the internal file version to 2.0.145.1 so existing 2.0 stable clients recognize the patch. Marketing version 2.0.5 and MSI version 2.0.145 stay fixed. The original updater's unchanged comparison sees 2.0.145.1 as newer than 2.0.145.0 and sees the installed patch as current. The MSI permits the same MSI-product-version replacement inside the rollback transaction. A machine with the unpublished 2.0.146 package is outside this upgrade path; do not silently downgrade it. All catalogs must report the real internal version 2.0.145.1; do not advertise an invented higher version for unchanged binaries.

The final MSI also bundles a Microsoft runtime, installer support helpers and company signatures. A local unsigned rebuild is not byte-identical to the signed release. The release assets identify the exact installer SHA256 and QA scope. Historical 1.x source and tags remain unchanged.
