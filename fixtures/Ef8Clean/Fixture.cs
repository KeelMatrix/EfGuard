using Microsoft.EntityFrameworkCore;

namespace EfGuard.Ef8CleanFixture;

public sealed class OrdersDbContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlServer();
}

public sealed class Order
{
    public int Id { get; set; }
}
