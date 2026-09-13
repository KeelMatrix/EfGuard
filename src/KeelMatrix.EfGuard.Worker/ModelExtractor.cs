using KeelMatrix.EfGuard;

namespace KeelMatrix.EfGuard.Worker;

internal static class ModelExtractor
{
    internal static ModelSnapshot ReadModel(object context)
    {
        object model = context.GetType().GetProperty("Model")?.GetValue(context) ?? throw new InvalidOperationException();
        IEnumerable<object> entities = Reflection.Enumerate(model, "GetEntityTypes");
        ModelSnapshot result = new();
        foreach (object entity in entities)
        {
            string table = Reflection.String(entity, "GetTableName") ?? Reflection.String(entity, "Name") ?? "Unknown";
            string? schema = Reflection.String(entity, "GetSchema");
            ModelTable modelTable = new() { Name = table, Schema = schema };
            foreach (object property in Reflection.Enumerate(entity, "GetProperties"))
            {
                modelTable.Columns.Add(new ModelColumn
                {
                    Name = Reflection.String(property, "GetColumnName") ?? Reflection.String(property, "Name") ?? "Unknown",
                    ClrType = Reflection.TypeName(property, "ClrType"),
                    IsNullable = Reflection.Bool(property, "IsNullable", defaultValue: true),
                    MaxLength = Reflection.Int(property, "GetMaxLength"),
                    Precision = Reflection.Byte(property, "GetPrecision"),
                    Scale = Reflection.Byte(property, "GetScale"),
                    Collation = Reflection.String(property, "GetCollation")
                });
            }
            result.Tables.Add(modelTable);
        }
        return result;
    }
}
