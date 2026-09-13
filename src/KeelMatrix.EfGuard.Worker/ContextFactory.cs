using System.Diagnostics;
using System.Reflection;

namespace KeelMatrix.EfGuard.Worker;

internal static class ContextFactory
{
    internal static ContextInstance? CreateContext(Type contextType, IEnumerable<Assembly> assemblies, string startupProjectPath)
    {
        ContextInstance? startupContext = CreateFromStartupServices(contextType, assemblies, startupProjectPath);
        if (startupContext is not null)
            return startupContext;

        return CreateFromDesignTimeFactoryOrConstructor(contextType, assemblies);
    }

    internal static string? ReadProvider(object context)
    {
        object? database = context.GetType().GetProperty("Database")?.GetValue(context);
        return database?.GetType().GetProperty("ProviderName")?.GetValue(database) as string;
    }

    internal static object? GetInfrastructureServiceProvider(object context)
    {
        Type? infrastructure = context.GetType().GetInterfaces().FirstOrDefault(type =>
            type.IsGenericType
            && type.GetGenericTypeDefinition().FullName == "Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure`1"
            && type.GetGenericArguments()[0] == typeof(IServiceProvider));
        return infrastructure?.GetProperty("Instance")?.GetValue(context);
    }

    private static ContextInstance? CreateFromStartupServices(Type contextType, IEnumerable<Assembly> assemblies, string startupProjectPath)
    {
        Assembly? startupAssembly = FindStartupAssembly(startupProjectPath, assemblies);
        if (startupAssembly is null)
            return null;

        object? host = TryBuildStartupHost(startupAssembly);
        if (host is null)
            return null;

        IServiceProvider? rootServices = GetServiceProvider(host);
        if (rootServices is null)
        {
            DisposeIfNeeded(host);
            return null;
        }

        IServiceProvider scopedServices = CreateScope(rootServices, out IDisposable? scope);
        object? context = null;
        bool contextCreatedByFactory = false;
        try
        {
            context = scopedServices.GetService(contextType);
            if (context is null)
            {
                Type? factoryType = assemblies
                    .Select(assembly => assembly.GetType("Microsoft.EntityFrameworkCore.IDbContextFactory`1"))
                    .FirstOrDefault(type => type is not null)?
                    .MakeGenericType(contextType);
                if (factoryType is not null)
                {
                    object? factory = scopedServices.GetService(factoryType);
                    MethodInfo? create = factory?.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(method => method.Name == "CreateDbContext" && method.GetParameters().Length == 0);
                    if (create is not null)
                    {
                        context = create.Invoke(factory, null);
                        contextCreatedByFactory = context is not null;
                    }
                }
            }
        }
        catch
        {
            context = null;
        }

        if (context is null)
        {
            DisposeIfNeeded(scope);
            DisposeIfNeeded(host);
            return null;
        }

        List<IDisposable> owners = [];
        if (contextCreatedByFactory)
            AddDisposable(owners, context);
        AddDisposable(owners, scope);
        AddDisposable(owners, host);
        return new ContextInstance(context, owners);
    }

    private static ContextInstance? CreateFromDesignTimeFactoryOrConstructor(Type contextType, IEnumerable<Assembly> assemblies)
    {
        Type? factoryType = assemblies.SelectMany(assembly => Reflection.GetTypesOrRecord(assembly))
            .Where(type => IsFactoryForContext(type, contextType))
            .OrderBy(type => type.Assembly == contextType.Assembly ? 0 : 1)
            .ThenBy(type => type.FullName, StringComparer.Ordinal)
            .FirstOrDefault();
        if (factoryType is not null)
        {
            object? factory = Activator.CreateInstance(factoryType);
            MethodInfo? method = factoryType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(m => m.Name == "CreateDbContext" && m.GetParameters().Length == 1);
            if (factory is not null && method is not null)
            {
                try
                {
                    object? context = method.Invoke(factory, [Array.Empty<string>()]);
                    return context is null ? null : new ContextInstance(context, [ToDisposable(context)]);
                }
                catch { throw new InvalidOperationException("The design-time factory failed during extraction."); }
            }
        }
        ConstructorInfo? constructor = contextType.GetConstructor(Type.EmptyTypes);
        if (constructor is null)
            return null;
        try
        {
            object? context = constructor.Invoke(null);
            return context is null ? null : new ContextInstance(context, [ToDisposable(context)]);
        }
        catch { throw new InvalidOperationException("The DbContext constructor failed during extraction."); }
    }

    private static Assembly? FindStartupAssembly(string startupProjectPath, IEnumerable<Assembly> assemblies)
    {
        string startupName = Path.GetFileNameWithoutExtension(startupProjectPath);
        return assemblies.FirstOrDefault(assembly => assembly.GetName().Name?.Equals(startupName, StringComparison.OrdinalIgnoreCase) == true)
            ?? assemblies.FirstOrDefault(assembly => assembly.EntryPoint is not null);
    }

    private static object? TryBuildStartupHost(Assembly startupAssembly)
    {
        Type? programType = startupAssembly.EntryPoint?.DeclaringType;
        if (programType is null)
            return null;

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (string methodName in new[] { "BuildWebHost", "CreateWebHostBuilder", "CreateHostBuilder" })
        {
            MethodInfo? factory = programType.GetMethods(flags).FirstOrDefault(method =>
                method.Name == methodName
                && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType == typeof(string[]));
            if (factory is null)
                continue;

            try
            {
                object? host = factory.Invoke(null, [Array.Empty<string>()]);
                if (methodName != "BuildWebHost")
                    host = BuildHost(host);
                if (host is not null && GetServiceProvider(host) is not null)
                    return host;
            }
            catch { }
        }

        return TryBuildHostFromEntryPoint(startupAssembly);
    }

    private static object? TryBuildHostFromEntryPoint(Assembly startupAssembly)
    {
        if (startupAssembly.EntryPoint is null)
            return null;

        try
        {
            Assembly.Load("Microsoft.Extensions.Hosting");
            using HostingListener listener = new(startupAssembly.EntryPoint, TimeSpan.FromSeconds(90), startupAssembly.GetName().Name);
            return listener.CreateHost();
        }
        catch { return null; }
    }

    private static object? BuildHost(object? builder)
    {
        if (builder is null)
            return null;
        MethodInfo? build = builder.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "Build" && method.GetParameters().Length == 0);
        return build?.Invoke(builder, null);
    }

    private static IServiceProvider? GetServiceProvider(object host)
    {
        try
        {
            if (host is IServiceProvider provider)
                return provider;
            object? services = host.GetType().GetProperty("Services", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(host);
            if (services is IServiceProvider providerFromConcreteType)
                return providerFromConcreteType;

            foreach (string assemblyName in new[] { "Microsoft.Extensions.Hosting.Abstractions", "Microsoft.AspNetCore.Hosting.Abstractions" })
            {
                Type? hostContract = Assembly.Load(assemblyName).GetType(assemblyName.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ? "Microsoft.AspNetCore.Hosting.IWebHost" : "Microsoft.Extensions.Hosting.IHost");
                services = hostContract?.GetProperty("Services")?.GetValue(host);
                if (services is IServiceProvider providerFromContract)
                    return providerFromContract;
            }
            return null;
        }
        catch { return null; }
    }

    private static IServiceProvider CreateScope(IServiceProvider rootServices, out IDisposable? scope)
    {
        scope = null;
        Type? scopeFactoryType = null;
        try
        {
            scopeFactoryType = Assembly.Load("Microsoft.Extensions.DependencyInjection.Abstractions")
                .GetType("Microsoft.Extensions.DependencyInjection.IServiceScopeFactory");
        }
        catch { }

        if (scopeFactoryType is null)
            return rootServices;

        try
        {
            object? scopeFactory = rootServices.GetService(scopeFactoryType);
            MethodInfo? createScope = scopeFactory?.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => (method.Name == "CreateScope" || method.Name.EndsWith(".CreateScope", StringComparison.Ordinal)) && method.GetParameters().Length == 0);
            object? createdScope = createScope is null
                ? scopeFactoryType.GetMethod("CreateScope")?.Invoke(scopeFactory, null)
                : createScope.Invoke(scopeFactory, null);
            Type? scopeType = null;
            try { scopeType = scopeFactoryType.Assembly.GetType("Microsoft.Extensions.DependencyInjection.IServiceScope"); } catch { }
            IServiceProvider? scopedServices = (scopeType?.GetProperty("ServiceProvider") ?? createdScope?.GetType().GetProperty("ServiceProvider"))?.GetValue(createdScope) as IServiceProvider;
            if (createdScope is IDisposable disposable && scopedServices is not null)
            {
                scope = disposable;
                return scopedServices;
            }
            DisposeIfNeeded(createdScope);
        }
        catch { }
        return rootServices;
    }

    private static bool IsFactoryForContext(Type type, Type contextType)
    {
        try
        {
            return !type.IsAbstract
                && type.GetConstructor(Type.EmptyTypes) is not null
                && type.GetInterfaces().Any(interfaceType =>
                    interfaceType.IsGenericType
                    && interfaceType.GetGenericTypeDefinition().FullName == "Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory`1"
                    && interfaceType.GetGenericArguments()[0] == contextType);
        }
        catch { return false; }
    }

    private static void AddDisposable(List<IDisposable> owners, object? value)
    {
        if (value is IDisposable disposable)
            owners.Add(disposable);
    }

    private static IDisposable ToDisposable(object value) => value as IDisposable ?? new NoopDisposable();
    private static void DisposeIfNeeded(object? value)
    {
        try { if (value is IDisposable disposable) disposable.Dispose(); }
        catch { }
    }

    private sealed class HostingListener(MethodInfo entryPoint, TimeSpan waitTimeout, string? assemblyName) : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly MethodInfo entryPoint = entryPoint;
        private readonly TimeSpan waitTimeout = waitTimeout;
        private readonly string? assemblyName = assemblyName;
        private readonly TaskCompletionSource<object> hostSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AsyncLocal<HostingListener?> current = new();
        private IDisposable? diagnosticSubscription;

        internal object? CreateHost()
        {
            using IDisposable allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
            Thread thread = new(() => RunEntryPoint()) { IsBackground = true };
            thread.Start();
            if (!hostSource.Task.Wait(waitTimeout))
                throw new InvalidOperationException("The startup project's host did not become available within the extraction timeout.");
            return hostSource.Task.GetAwaiter().GetResult();
        }

        private void RunEntryPoint()
        {
            Exception? failure = null;
            try
            {
                current.Value = this;
                ParameterInfo[] parameters = entryPoint.GetParameters();
                if (parameters.Length == 0)
                    entryPoint.Invoke(null, null);
                else
                {
                    string[] args = assemblyName is null ? [] : ["--applicationName", assemblyName];
                    entryPoint.Invoke(null, [args]);
                }
                hostSource.TrySetException(new InvalidOperationException("The startup entry point exited without building a host."));
            }
            catch (TargetInvocationException exception) when (exception.InnerException?.GetType().Name == "HostAbortedException") { }
            catch (TargetInvocationException exception)
            {
                failure = exception.InnerException ?? exception;
                hostSource.TrySetException(failure);
            }
            catch (Exception exception)
            {
                failure = exception;
                hostSource.TrySetException(failure);
            }
            finally
            {
                current.Value = null;
                if (failure is not null)
                    hostSource.TrySetException(failure);
            }
        }

        public void OnNext(DiagnosticListener value)
        {
            if (current.Value == this && value.Name == "Microsoft.Extensions.Hosting")
                diagnosticSubscription = value.Subscribe(this);
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (current.Value != this)
                return;
            if (value.Key == "HostBuilt" && value.Value is not null)
            {
                hostSource.TrySetResult(value.Value);
                ThrowHostAborted();
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() => diagnosticSubscription?.Dispose();

        public void Dispose()
        {
            diagnosticSubscription?.Dispose();
        }

        private static void ThrowHostAborted()
        {
            Type? exceptionType = Type.GetType("Microsoft.Extensions.Hosting.HostAbortedException, Microsoft.Extensions.Hosting.Abstractions", throwOnError: false);
            if (exceptionType is not null && Activator.CreateInstance(exceptionType) is Exception exception)
                throw exception;
            throw new HostAbortedException();
        }

        private sealed class HostAbortedException : Exception { }
    }
}

internal sealed class ContextInstance(object context, IEnumerable<IDisposable> owners) : IDisposable
{
    private readonly IDisposable[] owners = owners.ToArray();

    internal object Context { get; } = context;

    public void Dispose()
    {
        for (int index = owners.Length - 1; index >= 0; index--)
        {
            try { owners[index].Dispose(); }
            catch { }
        }
    }
}

internal sealed class NoopDisposable : IDisposable
{
    public void Dispose() { }
}
