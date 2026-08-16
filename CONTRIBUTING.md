# Contributing to Deja

Thanks for your interest in contributing! 🎉

## Getting started

1. Fork and clone the repository.
2. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) (the library multi-targets
   net8.0/net9.0/net10.0; the newest SDK builds them all).
3. Build and test:

   ```bash
   dotnet build
   dotnet test
   ```

4. Try your changes in the demo app:

   ```bash
   dotnet run --project samples/Deja.Sample
   ```

## Guidelines

- **Open an issue first** for new features so we can discuss the API before you invest time.
- Keep the library **dependency-free** — no runtime package references.
- Every public API needs XML docs; the build fails on missing docs and on warnings.
- Add or update tests (xUnit) for any behavior change.
- Run `dotnet format` before committing — CI verifies formatting.

## Documentation site

The docs site (`docs/Deja.Docs`) is a Blazor WebAssembly SPA published to GitHub Pages. Because
crawlers that do not execute WebAssembly would otherwise see an empty page, the published output is
prerendered: every route's rendered DOM is baked into its own `index.html` at build time.

### Adding a page

1. Add the entry to `DocsNav` — the sidebar, search index, prev/next links, sitemap and prerender
   manifest all read from it.
2. Give the page **two** `@page` directives, English and German:

   ```razor
   @page "/guides/my-page"
   @page "/de/guides/my-page"
   ```

   Both language trees share one route table, so a missing `/de/` directive makes the German URL
   fall through to the not-found page. CI does not catch this — check the page loads under `/de/`.

3. Write both languages via `<Localized>` (block content) or `T("English", "Deutsch")` (short
   strings). `NavItem` carries `TitleDe`/`DescriptionDe` for the nav and metadata.
4. Build in-site links with `LanguageService.PathFor(href, Lang)` so navigation stays inside the
   current language tree. A hard-coded `href="/guides/…"` drops a German reader back into English.

### Running the full pipeline locally

```bash
dotnet publish docs/Deja.Docs/Deja.Docs.csproj -c Release -o publish
dotnet run --project docs/Deja.Docs.Seo -c Release -- publish/wwwroot https://lisihasaj.github.io/Deja/
cd docs/prerender && npm ci && npx playwright install chromium
node prerender.mjs ../../publish/wwwroot https://lisihasaj.github.io/Deja/
```

The SEO step writes per-route heads, `hreflang` pairs, `sitemap.xml` and `prerender-routes.json`;
the prerender step fills in each route's body. `dotnet run` alone is enough for day-to-day work —
the pipeline only matters when changing routing, metadata or the prerenderer itself.

## Pull requests

- Target the `main` branch.
- Describe *what* and *why*; link the related issue.
- CI must be green (build, format check, tests on all target frameworks).

## Release process (maintainers)

Releases are tag-driven: pushing a tag `vX.Y.Z` triggers the release workflow, which packs with
[MinVer](https://github.com/adamralph/minver) and publishes to NuGet.org, then creates a GitHub Release.

## Code of Conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). Be kind.
