# Repository scope

This public repository contains only the Windows client, neutral client tests, client documentation and client build/release workflows.

- Never add server sources, worker sources, deployment tools, server archives, production configuration, actual invoices or runtime data.
- Keep authentication seeds, credentials, private keys and session tokens out of source and examples. Authentication belongs to the separately operated import service.
- Use reserved example domains and invented devices/customers in tests and documentation. Do not copy private project-status notes or infrastructure addresses.
- Maintain empty startup fields, portable self-contained single-EXE publishing, permanent dark mode, optional fields and server-enforced target validation.
- Use .NET 10 and run the client tests on Windows. The repository guard and NuGet audit must pass before release.
- Releases contain only the versioned client EXE, a ZIP containing that EXE and SHA256SUMS.txt. Signing is not a release requirement for the internal deployment.
- Do not overwrite an already published release. Coordinate API changes with the private service maintainers.
