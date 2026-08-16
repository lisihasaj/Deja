using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Deja.Docs.Navigation;

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

string CommonTags(string title, string description, string canonical, string ogType, string? ogDescription = null)
{
    var t = WebUtility.HtmlEncode(title);
    var d = WebUtility.HtmlEncode(description);
    var og = WebUtility.HtmlEncode(ogDescription ?? description);
    var alt = WebUtility.HtmlEncode(SeoMeta.LandingTitle);

    return $"""
            <title>{t}</title>
            <meta name="description" content="{d}"/>
            <link rel="canonical" href="{canonical}"/>
            <meta name="robots" content="{Robots}"/>
            <meta property="og:type" content="{ogType}"/>
            <meta property="og:site_name" content="{SeoMeta.SiteName}"/>
            <meta property="og:locale" content="en_US"/>
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

var landingBlock = CommonTags(SeoMeta.LandingTitle, SeoMeta.LandingDescription, baseUrl, "website", SeoMeta.LandingOgDescription)
    + "\n" + JsonLd(SeoMeta.SoftwareJsonLd(baseUrl))
    + "\n" + JsonLd(SeoMeta.WebSiteJsonLd(baseUrl));

File.WriteAllText(indexPath, WithHead(landingBlock));
Console.WriteLine("index.html            ← landing head");

foreach (var item in DocsNav.All)
{
    var block = CommonTags(SeoMeta.TitleFor(item), item.Description, SeoMeta.CanonicalFor(baseUrl, item), "article")
        + "\n" + JsonLd(SeoMeta.BreadcrumbJsonLd(baseUrl, item));

    var dir = Path.Combine(wwwroot, item.Href.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "index.html"), WithHead(block));
    Console.WriteLine($"{item.Href}/index.html".PadRight(42) + "← prerendered head");
}

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
    .AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");

foreach (var href in DocsNav.All.Select(l => l.Href).Prepend(""))
{
    sitemap.AppendLine("  <url>");
    sitemap.AppendLine(CultureInfo.InvariantCulture, $"    <loc>{baseUrl}{href}</loc>");
    if (LastMod(href) is { } lastMod) sitemap.AppendLine(CultureInfo.InvariantCulture, $"    <lastmod>{lastMod}</lastmod>");
    sitemap.AppendLine(CultureInfo.InvariantCulture, $"    <changefreq>{(href.Length == 0 ? "weekly" : "monthly")}</changefreq>");
    sitemap.AppendLine(CultureInfo.InvariantCulture, $"    <priority>{PriorityFor(href)}</priority>");
    sitemap.AppendLine("  </url>");
}

sitemap.AppendLine("</urlset>");
File.WriteAllText(Path.Combine(wwwroot, "sitemap.xml"), sitemap.ToString());
Console.WriteLine($"sitemap.xml           ← {DocsNav.All.Count + 1} urls");

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
