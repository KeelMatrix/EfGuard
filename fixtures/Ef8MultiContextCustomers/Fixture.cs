using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.Ef8MultiContextCustomers;

public sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
}

public sealed class Customer
{
    public int Id { get; set; }
}

[Migration("20240702000000_DropCustomersLegacyName")]
public sealed class DropCustomersLegacyName : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("LegacyName", "Customers");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>("LegacyName", "Customers", nullable: true);
}
