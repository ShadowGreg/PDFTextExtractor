using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using ApiGateway.Data;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var config = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();

    var services = new ServiceCollection();
    services.AddDbContext<DocumentContext>(options =>
        options.UseNpgsql(config.GetConnectionString("DefaultConnection")));

    var serviceProvider = services.BuildServiceProvider();
    var context = serviceProvider.GetRequiredService<DocumentContext>();

    Log.Information("Starting database migration...");
    await context.Database.MigrateAsync();
    Log.Information("Migration completed successfully");

    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Migration failed");
    return 1;
}
