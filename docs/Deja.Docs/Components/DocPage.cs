using Deja.Docs.Services;
using Microsoft.AspNetCore.Components;

namespace Deja.Docs.Components;

/// <summary>
/// Base class for bilingual docs pages: receives the cascaded site language and offers
/// <see cref="T"/> for short strings. Block content switches via the <c>Localized</c> component.
/// </summary>
public abstract class DocPage : ComponentBase
{
    [CascadingParameter] public Language Lang { get; set; }

    protected bool De => Lang == Language.De;

    protected string T(string en, string de) => De ? de : en;
}
