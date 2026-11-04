# SMB Manager 2.0.5 client source

This snapshot contains the exact direct client, engine and contracts C# sources used for build37. It includes the published 1.7.12 legacy hash catalog and the single-instance activation fix. It excludes the separate asset-survey integration work.

Build on Windows with .NET 10 SDK:

    dotnet publish ClientDesktop/ClientDesktop.csproj -c Release -r win-x64 --self-contained false -p:FileVersion=2.0.145.0 -p:InformationalVersion=2.0.5+qa.37

The final MSI also bundles a Microsoft runtime, installer support helpers and company signatures. A local unsigned rebuild is not byte-identical to the signed release. The release assets identify the exact installer SHA256 and QA scope. Historical 1.x source and tags remain unchanged.