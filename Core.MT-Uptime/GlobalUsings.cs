// The domain entity 'Monitor' clashes with System.Threading.Monitor, which ImplicitUsings pulls in.
// Alias it globally so a bare 'Monitor' always means our entity. We synchronize with the `lock`
// keyword (never the Monitor class by name), so shadowing System.Threading.Monitor is harmless.
// A using-alias directive wins over both the namespace import below and System.Threading, so
// 'Monitor' is never ambiguous.
global using MT.Uptime.Core.Domain;
global using Monitor = MT.Uptime.Core.Domain.Monitor;
