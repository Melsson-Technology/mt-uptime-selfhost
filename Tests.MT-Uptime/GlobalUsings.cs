// Match the Core/Web projects: 'Monitor' means our entity, not System.Threading.Monitor
// (ImplicitUsings pulls in System.Threading, so the bare name is otherwise ambiguous).
global using Monitor = MT.Uptime.Core.Domain.Monitor;
