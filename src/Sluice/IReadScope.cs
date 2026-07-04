namespace Sluice;

public interface IReadScope
{
    public Task<T> Track<T>(ResourceAddress address, Func<CancellationToken, Task<T>> work);

    public Task<T> Track<T>(ResourceAddress address, Func<Task<T>> work);

    public TimeProvider Clock { get; }

    public CancellationToken CancellationToken { get; }
}
