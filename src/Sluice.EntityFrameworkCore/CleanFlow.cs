using Microsoft.Extensions.DependencyInjection;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// Runs code on the same thread with none of the caller's async state: no <see cref="AsyncLocal{T}"/> values, so no
/// <c>IHttpContextAccessor</c> request, <c>Activity.Current</c>, culture set through the flow, ambient
/// <c>TransactionScope</c> with async flow, or running compute. A compute's contexts and the services its configure
/// asks for are made this way, so what they keep is what the cache key and the configure gave them, not the first
/// caller's request (SLUICE-EF-022).
/// </summary>
internal static class CleanFlow
{
    // An empty execution context, captured once from a thread started with flow suppressed, which starts with none.
    private static readonly ExecutionContext _empty = Capture();

    private static ExecutionContext Capture()
    {
        ExecutionContext? empty = null;
        var thread = new Thread(() => empty = ExecutionContext.Capture());
        using (ExecutionContext.SuppressFlow())
        {
            thread.Start();
        }

        thread.Join();
        return empty!;
    }

    /// <summary>
    /// <paramref name="work"/>'s result for <paramref name="arg"/>, run without the caller's async state. What it sets
    /// in the flow is dropped when it returns; what it throws reaches the caller.
    /// </summary>
    internal static TResult Run<TArg, TResult>(Func<TArg, TResult> work, TArg arg)
    {
        var call = new Call<TArg, TResult>(work, arg);
        ExecutionContext.Run(_empty, static call => ((Call<TArg, TResult>)call!).Invoke(), call);
        return call.Result!;
    }

    /// <summary>
    /// <paramref name="services"/>, resolving every service without the caller's async state, so a scoped service a
    /// configure asks for first doesn't keep the caller's request. Scopes made from it are the app's, as usual.
    /// </summary>
    internal static IServiceProvider Services(IServiceProvider services) => new Clean(services);

    private sealed class Call<TArg, TResult>(Func<TArg, TResult> work, TArg arg)
    {
        internal TResult? Result { get; private set; }

        internal void Invoke() => Result = work(arg);
    }

    // Every way MS.DI's extensions resolve: GetService, GetRequiredService through ISupportRequiredService, and the
    // keyed overloads through IKeyedServiceProvider.
    private sealed class Clean(IServiceProvider services)
        : IServiceProvider,
            ISupportRequiredService,
            IKeyedServiceProvider
    {
        public object? GetService(Type serviceType) =>
            Run(
                static state => state.services.GetService(state.serviceType),
                (services, serviceType)
            );

        public object GetRequiredService(Type serviceType) =>
            Run(
                static state => state.services.GetRequiredService(state.serviceType),
                (services, serviceType)
            );

        public object? GetKeyedService(Type serviceType, object? serviceKey) =>
            Run(
                static state =>
                    state.services is IKeyedServiceProvider keyed
                        ? keyed.GetKeyedService(state.serviceType, state.serviceKey)
                        : throw new InvalidOperationException(
                            "The application's service provider doesn't support keyed services."
                        ),
                (services, serviceType, serviceKey)
            );

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey) =>
            Run(
                static state =>
                    state.services.GetRequiredKeyedService(state.serviceType, state.serviceKey),
                (services, serviceType, serviceKey)
            );
    }
}
