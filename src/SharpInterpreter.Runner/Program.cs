// SharpInterpreter — Licensed under the MIT License.

using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// No UI: this page only hosts the .NET runtime that Interop.Run uses (see wwwroot/runner.js).
await WebAssemblyHostBuilder.CreateDefault(args).Build().RunAsync();
