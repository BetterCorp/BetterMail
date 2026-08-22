using System.Reflection;

namespace BetterMail.Core;

public static class BuildCredential
{
    public static string Require<T>(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            value = typeof(T).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == name)?.Value;
        }
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Build credential '{name}' is not configured.")
            : value.Trim();
    }
}
