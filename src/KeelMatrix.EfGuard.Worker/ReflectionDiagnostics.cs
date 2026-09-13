using System.Reflection;

namespace KeelMatrix.EfGuard.Worker;

/// <summary>
/// A fail-closed extraction failure that carries a message which is safe and useful to show the user, in
/// contrast to unexpected exceptions that stay behind the worker's generic stage failure text.
/// </summary>
internal sealed class ExtractionFailureException(string message) : Exception(message);

internal static class Reflection
{
    private const int NotesLimit = 10;
    private const int NoteTextLimit = 400;

    // Assemblies whose type enumeration did not complete. EfGuard reads the selected context's own assembly
    // and its attributed migration classes from these assemblies, so an unreadable one of those is fatal;
    // every other copied assembly is peripheral to migration attribution and is skipped instead.
    private static readonly Dictionary<string, string> unreadableAssemblies = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> notes = new(StringComparer.Ordinal);
    private static readonly HashSet<string> fatalAssemblies = new(StringComparer.Ordinal);
    private static IReadOnlyList<Assembly> assemblies = [];

    internal static void SetAssemblies(IReadOnlyList<Assembly> loadedAssemblies)
    {
        assemblies = loadedAssemblies;
        unreadableAssemblies.Clear();
        notes.Clear();
        fatalAssemblies.Clear();
    }

    /// <summary>
    /// Reads the loadable types of one copied assembly. Type enumeration failures are recorded per assembly
    /// instead of aborting the extraction, because the worker builds the scanned project with copied
    /// dependencies: design-time packages such as <c>Microsoft.EntityFrameworkCore.Design</c> copy
    /// MSBuild/Roslyn support assemblies that are not loadable in the worker's process yet have nothing to do
    /// with the scanned context's model or migrations. Failures are still fatal when they hit an assembly
    /// EfGuard must read, which callers enforce through <see cref="EnsureTypesReadable"/>.
    /// </summary>
    internal static Type[] GetTypesOrRecord(Assembly assembly, string? requestedMember = null)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // The loader kept every type it could resolve, so peripheral assemblies can still be used where
            // appropriate. Selected context and migrations assemblies are marked unreadable and rejected by
            // their callers instead of silently producing a partial report.
            RecordUnreadable(assembly, exception);
            RecordNote(assembly, requestedMember, exception);
            return exception.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }
        catch (Exception exception)
        {
            RecordUnreadable(assembly, exception);
            RecordNote(assembly, requestedMember, exception);
            return [];
        }
    }

    internal static bool TryGetTypes(Assembly assembly, out Type[] types)
    {
        types = GetTypesOrRecord(assembly);
        bool readable = !unreadableAssemblies.ContainsKey(Key(assembly));
        if (!readable)
            fatalAssemblies.Add(Key(assembly));
        return readable;
    }

    /// <summary>
    /// Fails closed when an assembly whose metadata EfGuard must read could not be enumerated at all.
    /// </summary>
    internal static void EnsureTypesReadable(Assembly assembly, string role)
    {
        _ = GetTypesOrRecord(assembly);
        if (unreadableAssemblies.ContainsKey(Key(assembly)))
        {
            fatalAssemblies.Add(Key(assembly));
            throw new ExtractionFailureException(UnreadableAssemblyMessage(assembly, role));
        }
    }

    internal static string UnreadableAssemblyMessage(Assembly assembly, string role)
        => "EF metadata could not be inspected for " + role + " '" + DisplayName(assembly)
            + "' because enumerating its types failed with " + (unreadableAssemblies.TryGetValue(Key(assembly), out string? failure) ? failure : "an unknown reflection failure")
            + ". EfGuard fails closed instead of reporting a partial migration scan, because it has to read that assembly's types to attribute the selected DbContext's migrations. "
            + "Next step: rebuild the project and run EfGuard again ('dotnet build', then 'efguard check'); if the failure persists, resolve the reported exception, which usually means the assembly needs a missing dependency or a runtime it was not built for.";

    /// <summary>
    /// Describes assemblies whose metadata could not be enumerated, for failures that are only explained by
    /// unreadable metadata (for example a DbContext that would have been found in a skipped assembly).
    /// </summary>
    internal static string DescribeUnreadableAssemblies()
    {
        if (unreadableAssemblies.Count == 0)
            return "";

        string listed = string.Join(", ", unreadableAssemblies.Keys.Take(3).Select(key => "'" + key + "'"));
        return " EfGuard could not enumerate the types of " + unreadableAssemblies.Count + " assembly(ies) of the copied dependency graph (" + listed
            + "), so any DbContext declared there would not be visible; rebuild the project and run EfGuard again, and resolve the reported exception if it persists.";
    }

    internal static void WriteRecordedNotes()
    {
        foreach (string note in RecordedNotes())
        {
            try { Console.Error.WriteLine("EfGuard note: " + note); }
            catch { }
        }
    }

    internal static List<string> RecordedNotes()
        => notes.Where(pair => !fatalAssemblies.Contains(pair.Key)).Select(pair => pair.Value).Take(NotesLimit).ToList();

    internal static object? Value(object instance, string name) => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);
    internal static string? String(object instance, string name) => Value(instance, name)?.ToString() ?? InvokeNoArgument(instance, name)?.ToString();
    internal static bool Bool(object instance, string name, bool defaultValue = false) => Value(instance, name) is bool value ? value : InvokeNoArgument(instance, name) is bool invoked ? invoked : defaultValue;
    internal static int? Int(object instance, string name) => Value(instance, name) is int value ? value : InvokeNoArgument(instance, name) is int invoked ? invoked : null;
    internal static byte? Byte(object instance, string name) => Value(instance, name) switch { byte value => value, int value when value is >= 0 and <= byte.MaxValue => (byte)value, _ => InvokeNoArgument(instance, name) switch { byte invoked => invoked, int invoked when invoked is >= 0 and <= byte.MaxValue => (byte)invoked, _ => null } };
    internal static string? TypeName(object instance, string name) => (Value(instance, name) as Type)?.FullName;

    internal static IEnumerable<object> Enumerate(object instance, string methodName)
    {
        object? value = InvokeNoArgument(instance, methodName);
        return value is System.Collections.IEnumerable enumerable ? enumerable.Cast<object>() : [];
    }

    internal static object? InvokeNoArgument(object instance, string methodName)
    {
        MethodInfo? method = instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == 0);
        if (method is not null)
            return method.Invoke(instance, null);

        foreach (Type interfaceType in instance.GetType().GetInterfaces())
        {
            MethodInfo? interfaceMethod = interfaceType.GetMethods().FirstOrDefault(candidate => candidate.Name == methodName && candidate.GetParameters().Length == 0);
            if (interfaceMethod is not null)
            {
                try { return interfaceMethod.Invoke(instance, null); }
                catch { }
            }
        }

        foreach (MethodInfo extension in assemblies.SelectMany(assembly =>
                     GetTypesOrRecord(assembly, methodName).Where(type => type.IsAbstract && type.IsSealed).SelectMany(StaticMethods))
                     .Where(candidate => candidate.Name == methodName && HasSingleParameter(candidate)))
        {
            ParameterInfo parameter = extension.GetParameters()[0];
            if (parameter.ParameterType.IsAssignableFrom(instance.GetType()))
            {
                try { return extension.Invoke(null, [instance]); }
                catch { }
            }
        }
        return null;
    }

    private static IEnumerable<MethodInfo> StaticMethods(Type type)
    {
        try { return type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
        catch { return []; }
    }

    private static bool HasSingleParameter(MethodInfo method)
    {
        try { return method.GetParameters().Length == 1; }
        catch { return false; }
    }

    private static void RecordUnreadable(Assembly assembly, Exception exception)
    {
        string description = exception is ReflectionTypeLoadException partial
            ? partial.GetType().Name + ": " + (partial.LoaderExceptions.Where(error => error is not null).Select(Describe).FirstOrDefault() ?? Describe(partial))
            : Describe(exception);
        unreadableAssemblies[Key(assembly)] = description.Length <= NoteTextLimit ? description : description[..NoteTextLimit];
    }

    private static void RecordNote(Assembly assembly, string? requestedMember, Exception exception)
    {
        ReflectionTypeLoadException? partial = exception as ReflectionTypeLoadException;
        string reason = partial is null
            ? Describe(exception)
            : partial.GetType().Name + ": " + (partial.LoaderExceptions.Where(error => error is not null).Select(Describe).FirstOrDefault() ?? Describe(exception));
        string outcome = "Skipped unreadable peripheral assembly '";
        notes[Key(assembly)] = outcome + DisplayName(assembly) + "'"
            + (requestedMember is null ? "" : " while resolving '" + requestedMember + "'") + " because " + reason;
    }

    private static string Describe(Exception? exception)
    {
        if (exception is null)
            return "an unknown reflection failure";

        string message = exception.Message.ReplaceLineEndings(" ");
        if (message.Length > NoteTextLimit)
            message = message[..NoteTextLimit];
        return exception.GetType().Name + ": " + message;
    }

    private static string Key(Assembly assembly) => assembly.FullName ?? assembly.GetName().Name ?? "";
    private static string DisplayName(Assembly assembly) => Key(assembly);

    internal static bool AnnotationBool(object instance, string annotationName)
    {
        try
        {
            IEnumerable<object> annotations = Enumerate(instance, "GetAnnotations");
            object? annotation = annotations.FirstOrDefault(a => String(a, "Name")?.Equals(annotationName, StringComparison.OrdinalIgnoreCase) == true);
            object? value = annotation is null ? null : Value(annotation, "Value");
            return value is bool boolean && boolean;
        }
        catch { return false; }
    }
}
