using System.Reflection;
using Xunit;

var testMethods = Assembly.GetExecutingAssembly()
    .GetTypes()
    .Where(type => type.IsClass && !type.IsAbstract)
    .SelectMany(type => type
        .GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Where(method => method.GetCustomAttribute<FactAttribute>() is not null)
        .Select(method => (Type: type, Method: method)))
    .ToArray();

var failures = new List<string>();

foreach (var test in testMethods)
{
    try
    {
        var instance = Activator.CreateInstance(test.Type)
            ?? throw new InvalidOperationException($"Cannot create {test.Type.Name}.");
        var result = test.Method.Invoke(instance, null);

        if (result is Task task)
            await task;

        Console.WriteLine($"PASS {test.Type.Name}.{test.Method.Name}");
    }
    catch (Exception exception)
    {
        var actual = exception is TargetInvocationException { InnerException: not null }
            ? exception.InnerException
            : exception;
        failures.Add($"FAIL {test.Type.Name}.{test.Method.Name}: {actual.Message}");
    }
}

foreach (var failure in failures)
    Console.Error.WriteLine(failure);

Console.WriteLine($"Total: {testMethods.Length}, Passed: {testMethods.Length - failures.Count}, Failed: {failures.Count}");
return failures.Count == 0 ? 0 : 1;
