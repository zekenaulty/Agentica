namespace Agentica.Tests;

public sealed class ContainerContractTests
{
    [Fact]
    public void Lab_container_uses_immutable_images_and_locked_restore()
    {
        var root = FindSolutionRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "Agentica.Lab", "Dockerfile"));

        var fromLines = dockerfile
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("FROM ", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(3, fromLines.Length);
        Assert.Contains(
            fromLines,
            line => line.StartsWith("FROM mcr.microsoft.com/dotnet/runtime:10.0.10@sha256:", StringComparison.Ordinal));
        Assert.Contains(
            fromLines,
            line => line.StartsWith("FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            fromLines,
            line => line.Contains("mcr.microsoft.com", StringComparison.Ordinal) &&
                !line.Contains("@sha256:", StringComparison.Ordinal));
        Assert.Contains("dotnet restore", dockerfile, StringComparison.Ordinal);
        Assert.Contains("--locked-mode", dockerfile, StringComparison.Ordinal);
        Assert.Contains("--configfile NuGet.config", dockerfile, StringComparison.Ordinal);
        Assert.Contains("COPY [\"NuGet.config\", \".\"]", dockerfile, StringComparison.Ordinal);
        Assert.Contains("COPY [\".editorconfig\", \".\"]", dockerfile, StringComparison.Ordinal);
        Assert.Contains("dotnet publish", dockerfile, StringComparison.Ordinal);
        Assert.Contains("--no-restore", dockerfile, StringComparison.Ordinal);
        Assert.Contains("Internal research harness; not a supported product CLI", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void Docker_context_excludes_credentials_build_outputs_and_live_evidence()
    {
        var root = FindSolutionRoot();
        var ignoreLines = File.ReadAllLines(Path.Combine(root, ".dockerignore"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(".env", ignoreLines);
        Assert.Contains(".env.*", ignoreLines);
        Assert.DoesNotContain("!.env.example", ignoreLines);
        Assert.Contains(".agentica", ignoreLines);
        Assert.Contains("docs", ignoreLines);
        Assert.Contains("**/bin", ignoreLines);
        Assert.Contains("**/obj", ignoreLines);
        Assert.Contains("Agentica.Tests", ignoreLines);
    }

    [Fact]
    public void Release_workflow_builds_and_smoke_tests_without_publishing_the_container()
    {
        var root = FindSolutionRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        Assert.Contains(
            "docker build --file Agentica.Lab/Dockerfile --tag agentica-lab:ci .",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "docker run --rm agentica-lab:ci quest list",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("--results=verified,unknown,unverified", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--only-verified", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("docker push", workflow, StringComparison.OrdinalIgnoreCase);

        var actionReferences = workflow
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("uses:", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf('@') + 1)..].Split(' ', 2)[0])
            .ToArray();
        Assert.NotEmpty(actionReferences);
        Assert.All(
            actionReferences,
            reference => Assert.True(
                reference.Length == 40 && reference.All(Uri.IsHexDigit),
                $"Workflow action reference '{reference}' is not pinned to a full commit SHA."));
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Agentica.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the Agentica solution root.");
    }
}
