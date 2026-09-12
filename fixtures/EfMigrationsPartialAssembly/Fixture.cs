using Microsoft.EntityFrameworkCore;

namespace EfGuard.EfMigrationsPartialAssembly;

public sealed class OrdersDbContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseSqlServer(options => options.MigrationsAssembly("BrokenMigrations"));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(order => order.Id);
        });
    }
}

public sealed class Order
{
    public int Id { get; set; }
}
