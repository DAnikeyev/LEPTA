namespace LEPTA.Tests;

internal sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly Action<T> handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public void Report(T value) => handler(value);
}

