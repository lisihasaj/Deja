using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Deja.Docs.Navigation;
using Deja.Docs.Services;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Deja.Docs.Seo <publish-wwwroot> <base-url>");
    Console.Error.WriteLine("  e.g. Deja.Docs.Seo publish/wwwroot https://lisihasaj.github.io/Deja/");
    return 1;
}

var wwwroot = Path.GetFullPath(args[0]);
var baseUrl = args[1].EndsWith('/') ? args[1] : args[1] + '/';
var basePath = new Uri(baseUrl).AbsolutePath;

var indexPath = Path.Combine(wwwroot, "index.html");
if (!File.Exists(indexPath))
{
    Console.Error.WriteLine($"not found: {indexPath}");
    return 1;
}

var shell = File.ReadAllText(indexPath)
    .Replace("<base href=\"/\"/>", $"<base href=\"{basePath}\"/>");

var seoBlock = new Regex("<!--seo-->.*?<!--/seo-->", RegexOptions.Singleline);
if (!seoBlock.IsMatch(shell))
{
    Console.Error.WriteLine("index.html has no <!--seo--> … <!--/seo--> block");
    return 1;
}

string WithHead(string block) => seoBlock.Replace(shell, $"<!--seo-->\n{block}\n    <!--/seo-->");

const string Robots = "index, follow, max-snippet:-1, max-image-preview:large, max-video-preview:-1";
var ogImage = SeoMeta.OgImageUrl(baseUrl);

// The German site lives under /de/ so crawlers can reach it; the client-side toggle only ever
// switched a localStorage flag, which left every translated page invisible to non-JS clients.
const string DePrefix = "de/";

static string PathFor(string href, Language lang) =>
    lang == Language.De ? DePrefix + href : href;

string UrlFor(string href, Language lang) => baseUrl + PathFor(href, lang);

string AlternateTags(string href) => $"""
        <link rel="alternate" hreflang="en" href="{UrlFor(href, Language.En)}"/>
            <link rel="alternate" hreflang="de" href="{UrlFor(href, Language.De)}"/>
            <link rel="alternate" hreflang="x-default" href="{UrlFor(href, Language.En)}"/>
    """;

string CommonTags(string title, string description, string canonical, string ogType, Language lang, string href, string? ogDescription = null)
{
    var t = WebUtility.HtmlEncode(title);
    var d = WebUtility.HtmlEncode(description);
    var og = WebUtility.HtmlEncode(ogDescription ?? description);
    var alt = WebUtility.HtmlEncode(lang == Language.De ? SeoMeta.LandingTitleDe : SeoMeta.LandingTitle);
    var locale = lang == Language.De ? "de_DE" : "en_US";

    return $"""
            <title>{t}</title>
            <meta name="description" content="{d}"/>
            <link rel="canonical" href="{canonical}"/>
            {AlternateTags(href)}
            <meta name="robots" content="{Robots}"/>
            <meta property="og:type" content="{ogType}"/>
            <meta property="og:site_name" content="{SeoMeta.SiteName}"/>
            <meta property="og:locale" content="{locale}"/>
            <meta property="og:title" content="{t}"/>
            <meta property="og:description" content="{og}"/>
            <meta property="og:url" content="{canonical}"/>
            <meta property="og:image" content="{ogImage}"/>
            <meta property="og:image:width" content="1200"/>
            <meta property="og:image:height" content="630"/>
            <meta property="og:image:alt" content="{alt}"/>
            <meta name="twitter:card" content="summary_large_image"/>
            <meta name="twitter:title" content="{t}"/>
            <meta name="twitter:description" content="{og}"/>
            <meta name="twitter:image" content="{ogImage}"/>
            <meta name="twitter:image:alt" content="{alt}"/>
        """;
}

static string JsonLd(string json) => $"    <script type=\"application/ld+json\">{json}</script>";

// The prerenderer replaces the body marker per route; it needs the file list and the language
// each file must be rendered in.
var manifest = new List<string>();

void WriteRoute(string href, Language lang, string head)
{
    var path = PathFor(href, lang);
    var html = WithHead(head);

    if (lang == Language.De)
    {
        html = html.Replace("<html lang=\"en\">", "<html lang=\"de\">");
    }

    string file;
    if (path.Length == 0)
    {
        file = "index.html";
    }
    else
    {
        var dir = Path.Combine(wwwroot, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        file = path + "/index.html";
    }

    File.WriteAllText(Path.Combine(wwwroot, file.Replace('/', Path.DirectorySeparatorChar)), html);
    manifest.Add($$"""{"href":"{{path}}","lang":"{{(lang == Language.De ? "de" : "en")}}","file":"{{file}}"}""");
    Console.WriteLine(file.PadRight(46) + $"← head [{(lang == Language.De ? "de" : "en")}]");
}

foreach (var lang in new[] { Language.En, Language.De })
{
    var de = lang == Language.De;

    var landingBlock = CommonTags(
            de ? SeoMeta.LandingTitleDe : SeoMeta.LandingTitle,
            de ? SeoMeta.LandingDescriptionDe : SeoMeta.LandingDescription,
            UrlFor("", lang), "website", lang, "",
            de ? SeoMeta.LandingOgDescriptionDe : SeoMeta.LandingOgDescription)
        + "\n" + JsonLd(SeoMeta.SoftwareJsonLd(baseUrl))
        + "\n" + JsonLd(SeoMeta.WebSiteJsonLd(baseUrl));

    WriteRoute("", lang, landingBlock);

    foreach (var item in DocsNav.All)
    {
        var block = CommonTags(
                SeoMeta.TitleFor(item, lang), item.DescriptionFor(lang),
                UrlFor(item.Href, lang), "article", lang, item.Href)
            + "\n" + JsonLd(SeoMeta.BreadcrumbJsonLd(baseUrl, item));

        WriteRoute(item.Href, lang, block);
    }
}

File.WriteAllText(
    Path.Combine(wwwroot, "prerender-routes.json"),
    "[\n  " + string.Join(",\n  ", manifest) + "\n]\n");
Console.WriteLine($"prerender-routes.json ← {manifest.Count} routes");

// GitHub Pages serves 404.html with a 200 for unknown SPA routes; noindex keeps junk URLs out
var notFoundBlock = $"""
        <title>Page not found — Deja</title>
        <meta name="robots" content="noindex, follow"/>
    """;
File.WriteAllText(Path.Combine(wwwroot, "404.html"), WithHead(notFoundBlock));
Console.WriteLine("404.html              ← SPA fallback, noindex");

var repoRoot = Git("rev-parse --show-toplevel", Environment.CurrentDirectory);

string? LastMod(string href)
{
    if (repoRoot is null) return null;

    var relPath = href.Length == 0 ? "docs/Deja.Docs/Pages/Home.razor" : RazorPathFor(href);
    var date = Git($"log -1 --format=%cs -- {relPath}", repoRoot);
    return string.IsNullOrWhiteSpace(date) ? null : date;
}

static string RazorPathFor(string href)
{
    var parts = href.Split('/');
    var dir = parts[0] switch
    {
        "getting-started" => "GettingStarted",
        "guides" => "Guides",
        "demos" => "Demos",
        "api" => "Api",
        _ => throw new ArgumentException($"unknown section in href '{href}'"),
    };

    var name = string.Concat(parts[1].Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    if (parts[0] == "api") name += "Page";

    return $"docs/Deja.Docs/Pages/{dir}/{name}.razor";
}

static string PriorityFor(string href) => href switch
{
    "" => "1.0",
    "getting-started/introduction" or "getting-started/quick-start" => "0.9",
    "getting-started/installation" => "0.8",
    _ when href.StartsWith("demos/", StringComparison.Ordinal) => "0.8",
    _ when href.StartsWith("guides/", StringComparison.Ordinal) => "0.7",
    _ => "0.6",
};

var sitemap = new StringBuilder()
    .AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>")
    .AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\" xmlns:xhtml=\"http://www.w3.org/1999/xhtml\">");

var urls = 0;

foreach (var href in DocsNav.All.Select(l => l.Href).Prepend(""))
{
    var lastMod = LastMod(href);

    foreach (var lang in new[] { Language.En, Language.De })
    {
        sitemap.AppendLine("  <url>");
        sitemap.AppendLine(CultureInfo.InvariantCulture, $"    <loc>{UrlFor(href, lang)}</loc>");
        sitemap.AppendLine(CultureInfo.InvariantCulture, $"    <xhtml:link rel=\"alternate\" hreflang=\"en\" href=\"{UrlFor(href, Language.En)}\"/>");
        sitemap.AppendLine(CultureInfo.InvariantCulture, $"    <xhtml:link rel=\"alternate\" hreflang=\"de\" href=\"{UrlFor(href, Language.De)}\"/>");
        sitemap.AppendLine(CultureInfo.InvariantCulture, $"    <xhtml:link rel=\"alternate\" hreflang=\"x-default\" href=\"{UrlFor(href, Language.En)}\"/>");
        if (lastMod is not null) sitemap.AppendLine(CultureInfo.InvariantCulture, $"    <lastmod>{lastMod}</lastmod>");
        sitemap.AppendLine(CultureInfo.InvariantCulture, $"    <changefreq>{(href.Length == 0 ? "weekly" : "monthly")}</changefreq>");
        sitemap.AppendLine(CultureInfo.InvariantCulture, $"    <priority>{PriorityFor(href)}</priority>");
        sitemap.AppendLine("  </url>");
        urls++;
    }
}

sitemap.AppendLine("</urlset>");
File.WriteAllText(Path.Combine(wwwroot, "sitemap.xml"), sitemap.ToString());
Console.WriteLine($"sitemap.xml           ← {urls} urls");

return 0;

static string? Git(string arguments, string workingDirectory)
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (process is null) return null;

        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : null;
    }
    catch (Exception)
    {
        return null;
    }
}
