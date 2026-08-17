using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MT.Uptime.Core.Data;

/// <summary>
/// Used only by the EF Core tools (<c>dotnet ef migrations …</c>). Its presence lets the tools
/// build the model without booting the web host — so startup side effects like
/// <see cref="DatabaseInitializer"/> never run at design time. The connection string is a throwaway;
/// <c>migrations add</c> inspects the model, it does not touch a real database.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=mt-uptime.design.db")
            .Options;
        return new AppDbContext(options);
    }
}
