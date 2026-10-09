namespace Agentica.Lab.Web.FixtureHost;

public static class FixtureHostOptions
{
    public static Uri ParseEndpoint(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? endpoint = null;
        for (var i = 0; i < args.Count; i++)
        {
            string? selected = null;
            if (args[i] == "--fixture-endpoint")
            {
                if (++i >= args.Count) throw new ArgumentException("--fixture-endpoint requires an explicit loopback HTTP URL.", nameof(args));
                selected = args[i];
            }
            else if (args[i].StartsWith("--fixture-endpoint=", StringComparison.Ordinal))
                selected = args[i]["--fixture-endpoint=".Length..];
            if (selected is null) continue;
            if (endpoint is not null) throw new ArgumentException("Specify --fixture-endpoint once.", nameof(args));
            endpoint = selected;
        }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new ArgumentException("--fixture-endpoint is mandatory and must be an absolute loopback HTTP URL.", nameof(args));
        return LoopbackFixturePlannerFactory.ValidateEndpoint(uri);
    }
}
