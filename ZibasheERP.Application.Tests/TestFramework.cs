namespace Xunit;

[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute;

public static class Assert
{
    public static void True(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Expected condition to be true.");
    }

    public static void False(bool condition) => True(!condition);

    public static void Null(object? value)
    {
        if (value is not null)
            throw new InvalidOperationException("Expected value to be null.");
    }

    public static void NotNull(object? value)
    {
        if (value is null)
            throw new InvalidOperationException("Expected value not to be null.");
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, but found {actual}.");
    }

    public static void NotEqual<T>(T notExpected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
            throw new InvalidOperationException($"Did not expect {actual}.");
    }

    public static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected text to contain '{expectedSubstring}'.");
    }

    public static void Contains<T>(IEnumerable<T> values, Predicate<T> predicate)
    {
        if (!values.Any(value => predicate(value)))
            throw new InvalidOperationException("Expected collection to contain a matching item.");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }
}
