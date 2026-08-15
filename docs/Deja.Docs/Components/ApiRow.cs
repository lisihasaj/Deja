namespace Deja.Docs.Components;

/// <summary>One row of an <c>ApiTable</c>. Description is markup so it can contain code ticks.</summary>
public sealed record ApiRow(string Name, string Type, string? Default, string Description);
