// Bring in the domain namespace, and alias the 'Monitor' entity (which clashes with
// System.Threading.Monitor from ImplicitUsings). The alias wins over both, so a bare
// 'Monitor' always means our entity in Web .cs and .razor files.
global using MT.Uptime.Core.Domain;
global using Monitor = MT.Uptime.Core.Domain.Monitor;
