using System.Reflection;
using System.Runtime.Loader;

namespace KeelMatrix.EfGuard.Worker;

internal static class AssemblyLoader
{
    internal static List<Assembly> LoadAssemblies(IEnumerable<string> paths, AssemblyLoadContext loadContext) => paths
        .SelectMany(path => Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.dll") : [])
        .Where(path => !IsSharedFrameworkAssembly(Path.GetFileNameWithoutExtension(path)))
        .Select(path => TryLoad(path, loadContext))
        .Where(a => a is not null)
        .Cast<Assembly>()
        .Distinct()
        .ToList();

    internal static Assembly? ResolveAssembly(AssemblyName name, IEnumerable<string> paths, AssemblyLoadContext loadContext)
    {
        if (IsSharedFrameworkAssembly(name.Name))
        {
            try { return AssemblyLoadContext.Default.LoadFromAssemblyName(name); }
            catch { }
        }

        return paths.Select(path => Path.Combine(path, name.Name + ".dll"))
            .Where(File.Exists)
            .Select(path => TryLoad(path, loadContext))
            .FirstOrDefault(a => a is not null)
            ?? AssemblyLoadContext.Default.LoadFromAssemblyName(name);
    }

    internal static bool IsSharedFrameworkAssembly(string? name)
        => name?.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) == true
            || name?.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) == true;

    private static Assembly? TryLoad(string path, AssemblyLoadContext loadContext) { try { return loadContext.LoadFromAssemblyPath(path); } catch { return null; } }
}

internal sealed class TargetLoadContext(IEnumerable<string> probingPaths) : AssemblyLoadContext("EfGuard.Target", isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (AssemblyLoader.IsSharedFrameworkAssembly(assemblyName.Name))
            return null;
        string? path = probingPaths.Select(directory => Path.Combine(directory, assemblyName.Name + ".dll")).FirstOrDefault(File.Exists);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
