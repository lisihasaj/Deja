using Deja;
using Deja.Docs;
using Deja.Docs.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton(new HttpClient
{
    BaseAddress = new Uri("https://jsonplaceholder.typicode.com/"),
});

builder.Services.AddSingleton<LatencySimulator>();
builder.Services.AddSingleton<FailureSwitch>();
builder.Services.AddSingleton<JsonPlaceholderApi>();

// Explicitly non-default, so the site demonstrates configuration. With the 10-second stale time,
// remounting a page within 10s serves the cache without a network request; after 10s the cache
// still renders instantly and revalidates in the background.
builder.Services.AddDeja(options =>
{
    options.DefaultStaleTime = TimeSpan.FromSeconds(10);
    options.DefaultCacheTime = TimeSpan.FromMinutes(5);
});

await builder.Build().RunAsync();
