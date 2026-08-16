using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace Deja.Docs.Services;

public enum Language { En, De }

/// <summary>
/// The site language, derived from the URL: German pages live under <c>/de/</c> so crawlers and
/// shared links resolve a real localized page instead of an English one that reskins itself. The
/// choice is mirrored to localStorage so returning to the site root restores the language last used.
/// </summary>
public sealed class LanguageService(IJSRuntime js, NavigationManager nav) : IDisposable
{
    private const string StorageKey = "deja-docs-lang";
    public const string DePrefix = "de";

    public Language Current { get; private set; } = Language.En;

    public event Action? Changed;

    public async Task InitializeAsync()
    {
        Current = FromPath(nav.ToBaseRelativePath(nav.Uri));
        nav.LocationChanged += OnLocationChanged;

        // A bare root URL honours the stored choice, so a bookmark of "/" stays German.
        if (Current == Language.En && LanguageService.RouteOf(nav.ToBaseRelativePath(nav.Uri)).Length == 0)
        {
            var stored = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (stored == "de")
            {
                Current = Language.De;
                nav.NavigateTo(PathFor(string.Empty, Language.De), forceLoad: false, replace: true);
            }
        }
    }

    /// <summary>Switches language by navigating to the same page in the other tree.</summary>
    public async Task SetAsync(Language language)
    {
        if (Current == language) return;

        await js.InvokeVoidAsync("dejaDocs.setLang", language == Language.De ? "de" : "en");
        nav.NavigateTo(PathFor(RouteOf(nav.ToBaseRelativePath(nav.Uri)), language), forceLoad: false);
    }

    /// <summary>Prefixes a language-neutral route for the given language.</summary>
    public static string PathFor(string route, Language language)
        => language == Language.De
            ? (route.Length == 0 ? DePrefix : $"{DePrefix}/{route}")
            : route;

    /// <summary>The language-neutral route, with any <c>de/</c> prefix and query/fragment removed.</summary>
    public static string RouteOf(string relativePath)
    {
        var route = relativePath;
        var cut = route.IndexOfAny(['?', '#']);
        if (cut >= 0) route = route[..cut];
        route = route.Trim('/');

        if (route.Equals(DePrefix, StringComparison.OrdinalIgnoreCase)) return string.Empty;

        return route.StartsWith(DePrefix + "/", StringComparison.OrdinalIgnoreCase)
            ? route[(DePrefix.Length + 1)..]
            : route;
    }

    private static Language FromPath(string relativePath)
    {
        var route = relativePath.Trim('/');
        return route.Equals(DePrefix, StringComparison.OrdinalIgnoreCase)
            || route.StartsWith(DePrefix + "/", StringComparison.OrdinalIgnoreCase)
                ? Language.De
                : Language.En;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var language = FromPath(nav.ToBaseRelativePath(nav.Uri));
        if (language == Current) return;

        Current = language;
        Changed?.Invoke();
    }

    public void Dispose() => nav.LocationChanged -= OnLocationChanged;
}
