using Microsoft.JSInterop;

namespace Deja.Docs.Services;

public enum Language { En, De }

/// <summary>
/// The site language, defaulting to English. The choice persists in localStorage and flows to
/// components as a cascading value from <c>App</c>, so switching re-renders the site in place.
/// </summary>
public sealed class LanguageService(IJSRuntime js)
{
    private const string StorageKey = "deja-docs-lang";

    public Language Current { get; private set; } = Language.En;

    public event Action? Changed;

    public async Task InitializeAsync()
    {
        var stored = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        if (stored == "de") Current = Language.De;
    }

    public async Task SetAsync(Language language)
    {
        if (Current == language) return;

        Current = language;
        await js.InvokeVoidAsync("dejaDocs.setLang", language == Language.De ? "de" : "en");
        Changed?.Invoke();
    }
}
