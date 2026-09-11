using EfGuard.EfStartupServicesFixture;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EfGuard.EfStartupServicesHost;

public static class Program
{
    public static IHostBuilder CreateHostBuilder(string[] args)
        => Host.CreateDefaultBuilder(args)
            .ConfigureServices(services => services.AddDbContext<StartupServicesDbContext>(options =>
                options.UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=EfGuardStartupServices;Trusted_Connection=True")));

    public static void Main(string[] args) => CreateHostBuilder(args).Build().Run();
}
