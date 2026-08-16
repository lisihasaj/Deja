using Deja.Docs.Services;

namespace Deja.Docs.Navigation;

/// <summary>
/// Shared SEO values and JSON-LD builders used by the runtime <c>PageMeta</c> head tags and the
/// build-time prerender/sitemap generator, so the two can never drift.
/// </summary>
public static class SeoMeta
{
    public const string SiteName = "Deja";
    public const string Author = "Lisjan Hasaj";
    public const string RepositoryUrl = "https://github.com/lisihasaj/Deja";
    public const string OgImagePath = "social/og-default.png";

    public const string LandingTitle = "Deja — data fetching and caching for Blazor";
    public const string LandingTitleDe = "Deja — Datenabruf und Caching für Blazor";

    public const string LandingDescription =
        "Data fetching, caching and synchronization for Blazor without the boilerplate. Declare a query, " +
        "bind loading and error state, and let a shared keyed cache dedupe requests.";

    public const string LandingDescriptionDe =
        "Datenabruf, Caching und Synchronisierung für Blazor ohne Boilerplate. Deklariere eine Query, binde " +
        "Lade- und Fehlerzustand, und ein gemeinsamer, keybasierter Cache dedupliziert die Requests.";

    public const string LandingOgDescription =
        "Declare a query, bind its loading and error state, and let a shared keyed cache dedupe the requests. " +
        "Blazor WebAssembly and Server, dependency-free.";

    public const string LandingOgDescriptionDe =
        "Deklariere eine Query, binde ihren Lade- und Fehlerzustand, und lass einen gemeinsamen, keybasierten " +
        "Cache die Requests deduplizieren. Blazor WebAssembly und Server, ohne Abhängigkeiten.";

    /// <summary>The document title, matching each page's <c>&lt;PageTitle&gt;</c> conventions.</summary>
    public static string TitleFor(NavItem item, Language lang = Language.En)
    {
        var title = item.TitleFor(lang);

        if (item.Href.StartsWith("api/", StringComparison.Ordinal))
        {
            return lang == Language.De ? $"{title} — Deja-API" : $"{title} — Deja API";
        }

        if (item.Href.StartsWith("demos/", StringComparison.Ordinal))
        {
            return lang == Language.De ? $"Demo: {title} — Deja" : $"{title} demo — Deja";
        }

        return $"{title} — Deja";
    }

    /// <summary>Absolute canonical URL for a nav item. <paramref name="baseUrl"/> must end with '/'.</summary>
    public static string CanonicalFor(string baseUrl, NavItem item) => baseUrl + item.Href;

    public static string OgImageUrl(string baseUrl) => baseUrl + OgImagePath;

    public static string SoftwareJsonLd(string baseUrl) =>
        $$"""
        {
          "@context": "https://schema.org",
          "@type": "SoftwareSourceCode",
          "name": "{{SiteName}}",
          "description": "Data fetching, mutation and caching primitives for Blazor — WebAssembly and Server.",
          "url": "{{baseUrl}}",
          "codeRepository": "{{RepositoryUrl}}",
          "programmingLanguage": "C#",
          "runtimePlatform": ".NET",
          "license": "https://opensource.org/licenses/MIT",
          "author": { "@type": "Person", "name": "{{Author}}" }
        }
        """;

    public static string WebSiteJsonLd(string baseUrl) =>
        $$"""
        {
          "@context": "https://schema.org",
          "@type": "WebSite",
          "name": "{{SiteName}}",
          "url": "{{baseUrl}}"
        }
        """;

    public static string BreadcrumbJsonLd(string baseUrl, NavItem item)
    {
        var section = DocsNav.Sections.First(s => s.Links.Contains(item));
        var sectionHref = baseUrl + section.Links[0].Href;

        return
            $$"""
            {
              "@context": "https://schema.org",
              "@type": "BreadcrumbList",
              "itemListElement": [
                { "@type": "ListItem", "position": 1, "name": "{{SiteName}}", "item": "{{baseUrl}}" },
                { "@type": "ListItem", "position": 2, "name": "{{item.Section}}", "item": "{{sectionHref}}" },
                { "@type": "ListItem", "position": 3, "name": "{{item.Title}}" }
              ]
            }
            """;
    }
}
