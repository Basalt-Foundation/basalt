using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Basalt.Explorer;
using Basalt.Explorer.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var nodeUrl = builder.Configuration["NodeUrl"] ?? builder.HostEnvironment.BaseAddress;
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(nodeUrl) });
builder.Services.AddScoped<BasaltApiClient>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped(sp => new Basalt.Explorer.Services.BlockWebSocketService(nodeUrl));

await builder.Build().RunAsync();
