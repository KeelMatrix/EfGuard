using EfGuard.MissingDependency;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.BrokenMigrations;

public sealed class BrokenMetadataType : MissingBase
{
}

[Migration("20250105000000_AddOrder")]
public sealed class AddOrder : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "Orders",
        columns: table => new
        {
            Id = table.Column<int>(type: "int", nullable: false)
        },
        constraints: table => table.PrimaryKey("PK_Orders", x => x.Id));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("Orders");
}
