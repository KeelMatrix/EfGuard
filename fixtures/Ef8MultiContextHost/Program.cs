using EfGuard.Ef8MultiContextCustomers;
using EfGuard.Ef8MultiContextOrders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EfGuard.Ef8MultiContextHost;

public static class Program
{
    public static IHostBuilder CreateHostBuilder(string[] args)
        => Host.CreateDefaultBuilder(args)
            .ConfigureServices(services =>
            {
                services.AddDbContext<OrdersDbContext>(options =>
                    options.UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=EfGuardMultiContextOrders;Trusted_Connection=True"));
                services.AddDbContext<CustomersDbContext>(options =>
                    options.UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=EfGuardMultiContextCustomers;Trusted_Connection=True"));
            });

    public static void Main(string[] args) => CreateHostBuilder(args).Build().Run();
}
