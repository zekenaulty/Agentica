using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentica.Clients.Anthropic;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Clients.Ollama;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Xai;

namespace Agentica.Tests;

public sealed class LlmNativeContinuationCodecTests
{
    private const string Instructions = "private-policy-marker Δ 😀";
    private const string Configuration = "sha256:captured-fixture-route";

    [Theory]
    [InlineData("openai")]
    [InlineData("xai")]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    [InlineData("Gemini")]
    [InlineData("Gemini.GenerateContent")]
    public void Every_existing_native_format_roundtrips_exactly_and_reaches_matching_request_body(string provider)
    {
        var history = History(provider);
        using var source = new LlmNativeContinuation(provider, "fixture-model", Instructions, history);
        using var binding = Binding(provider);
        var initial = LlmNativeContinuationCodec.ValidateBinding(source, binding);
        Assert.True(initial.IsCompatible);
        Assert.False(initial.ConfigurationBound);
        var exported = LlmNativeContinuationCodec.ExportSensitive(source, binding);
        using var payload = exported.SensitivePayload;
        Assert.True(exported.Succeeded, exported.Rejection.ToString());
        Assert.True(LlmNativeContinuationCodec.ValidateBinding(source, binding).ConfigurationBound);
        var imported = LlmNativeContinuationCodec.ImportSensitive(payload, binding);
        using var reopened = imported.Continuation;
        Assert.True(imported.Succeeded, imported.Rejection.ToString());
        Assert.Equal(history, reopened!.HistoryStepsJson);
        using var clone = reopened.CloneOwned();
        reopened.Dispose();
        Assert.True(LlmNativeContinuationCodec.ValidateBinding(clone, binding).ConfigurationBound);
        using var changed = Binding(provider, configuration: "different-route");
        Assert.Equal(LlmNativeContinuationRejection.ConfigurationMismatch,
            LlmNativeContinuationCodec.ValidateBinding(clone, changed).Rejection);
        Assert.Equal(LlmNativeContinuationRejection.ConfigurationMismatch,
            LlmNativeContinuationCodec.ExportSensitive(clone, changed).Rejection);

        var request = new LlmRequest("fixture-model", [new(LlmMessageRole.System, Instructions),
            new(LlmMessageRole.User, "continue")], NativeContinuation: clone);
        var body = Body(provider, request);
        var native = body.GetProperty(provider switch
        { "anthropic" or "ollama" => "messages", "Gemini.GenerateContent" => "contents", _ => "input" });
        using var expected = JsonDocument.Parse(history);
        Assert.Equal(expected.RootElement.GetArrayLength() + 1, native.GetArrayLength());
        for (var index = 0; index < expected.RootElement.GetArrayLength(); index++)
            Assert.True(JsonElement.DeepEquals(expected.RootElement[index], native[index]));
        if (provider is "openai" or "xai")
            Assert.Equal("final_answer", native[2].GetProperty("phase").GetString());
    }

    [Fact]
    public void Ordinary_serialization_and_display_never_export_private_material()
    {
        using var source = new LlmNativeContinuation("openai", "fixture-model", Instructions, History("openai"));
        using var binding = Binding("openai");
        var exported = LlmNativeContinuationCodec.ExportSensitive(source, binding);
        using var payload = exported.SensitivePayload;
        var imported = LlmNativeContinuationCodec.ImportSensitive(payload, binding);
        using var reopened = imported.Continuation;
        var json = JsonSerializer.Serialize(new
        {
            source,
            binding,
            exported,
            payload,
            imported,
            request = new LlmRequest("fixture-model", [], NativeContinuation: reopened),
            response = new LlmResponse("openai", "fixture-model", "public answer", NativeContinuation: reopened)
        });
        foreach (var secret in new[] { "private-policy-marker", "opaque-openai", "private-thought" })
        {
            Assert.DoesNotContain(secret, json);
            Assert.DoesNotContain(secret, source + " " + binding + " " + exported + " " + payload + " " + imported);
        }
        var bytes = payload!.CopySensitiveBytes();
        try { Assert.Contains("opaque-openai", Encoding.UTF8.GetString(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("xai")]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    [InlineData("Gemini")]
    [InlineData("Gemini.GenerateContent")]
    public void Configuration_reports_advertise_codec_and_binding_factory_preserves_instruction_projection(string provider)
    {
        var request = new LlmRequest("fixture-model", [new(LlmMessageRole.System, Instructions),
            new(LlmMessageRole.System, " "), new(LlmMessageRole.User, "start")]);
        var report = Report(provider, request);
        Assert.Equal(LlmCapabilitySupport.Supported,
            Assert.Single(report.Capabilities, capability => capability.Name == "durable_continuation_exchange").AdapterSupport);
        using var binding = LlmNativeContinuationBinding.FromRequest(report, request);
        Assert.Equal(provider == "ollama" ? Instructions + "\n\n " : Instructions, binding.SystemInstruction);
        Assert.Equal(report.ConfigurationIdentity, binding.ConfigurationIdentity);
        Assert.Throws<ArgumentException>(() => LlmNativeContinuationBinding.FromRequest(report, request with { ModelId = "other" }));
    }

    [Fact]
    public void Unresolved_legacy_SDK_report_cannot_become_a_replay_binding()
    {
        var request = new LlmRequest("fixture-model", [new(LlmMessageRole.User, "start")]);
        var report = LlmConfigurationReports.Describe(request, new GeminiClientOptions("fixture-key"));
        Assert.Equal(LlmCapabilitySupport.Unsupported,
            Assert.Single(report.Capabilities, capability => capability.Name == "durable_continuation_exchange").AdapterSupport);
        Assert.Throws<ArgumentException>(() => LlmNativeContinuationBinding.FromRequest(report, request));
    }

    [Fact]
    public void Unavailable_and_disposed_state_are_explicit_and_owned_clones_survive_source_disposal()
    {
        using var binding = Binding("openai");
        Assert.Equal(LlmNativeContinuationRejection.Unavailable,
            LlmNativeContinuationCodec.ExportSensitive(null, binding).Rejection);
        Assert.Equal(LlmNativeContinuationRejection.Unavailable,
            LlmNativeContinuationCodec.ImportSensitive(ReadOnlySpan<byte>.Empty, binding).Rejection);
        using var source = new LlmNativeContinuation("openai", "fixture-model", Instructions, History("openai"));
        using var clone = source.CloneOwned();
        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.CloneOwned());
        Assert.Equal(LlmNativeContinuationRejection.Disposed,
            LlmNativeContinuationCodec.ExportSensitive(source, binding).Rejection);
        var export = LlmNativeContinuationCodec.ExportSensitive(clone, binding);
        var payload = Assert.IsType<LlmSensitiveContinuationPayload>(export.SensitivePayload);
        payload.Dispose();
        Assert.Throws<ObjectDisposedException>(() => payload.CopySensitiveBytes());
        Assert.Equal(LlmNativeContinuationRejection.Disposed,
            LlmNativeContinuationCodec.ImportSensitive(payload, binding).Rejection);
        binding.Dispose();
        Assert.Equal(LlmNativeContinuationRejection.Disposed,
            LlmNativeContinuationCodec.ValidateBinding(clone, binding).Rejection);
    }

    [Theory]
    [InlineData("provider", LlmNativeContinuationRejection.ProviderMismatch)]
    [InlineData("api", LlmNativeContinuationRejection.UnsupportedApiMode)]
    [InlineData("model", LlmNativeContinuationRejection.ModelMismatch)]
    [InlineData("instructions", LlmNativeContinuationRejection.InstructionMismatch)]
    [InlineData("configuration", LlmNativeContinuationRejection.ConfigurationMismatch)]
    public void Import_rejects_changed_exact_target_bindings(string change, LlmNativeContinuationRejection expected)
    {
        using var source = new LlmNativeContinuation("openai", "fixture-model", Instructions, History("openai"));
        using var binding = Binding("openai");
        using var target = new LlmNativeContinuationBinding(change == "provider" ? "xai" : "openai",
            change == "api" ? "interactions" : "responses", change == "model" ? "another-model" : "fixture-model",
            change == "configuration" ? "another-configuration" : Configuration,
            change == "instructions" ? "changed instructions" : Instructions);
        var export = LlmNativeContinuationCodec.ExportSensitive(source, binding);
        using var payload = export.SensitivePayload;
        Assert.Equal(expected, LlmNativeContinuationCodec.ImportSensitive(payload, target).Rejection);
    }

    [Theory]
    [InlineData("version", LlmNativeContinuationRejection.UnsupportedVersion)]
    [InlineData("version_type", LlmNativeContinuationRejection.MalformedPayload)]
    [InlineData("api", LlmNativeContinuationRejection.ApiModeMismatch)]
    [InlineData("native", LlmNativeContinuationRejection.InvalidNativeShape)]
    [InlineData("instructions_size", LlmNativeContinuationRejection.PayloadTooLarge)]
    public void Malformed_or_incompatible_envelopes_are_rejected_even_with_recomputed_checksum(
        string change, LlmNativeContinuationRejection expected)
    {
        using var source = new LlmNativeContinuation("openai", "fixture-model", Instructions, History("openai"));
        using var binding = Binding("openai");
        var export = LlmNativeContinuationCodec.ExportSensitive(source, binding);
        using var payload = export.SensitivePayload;
        var bytes = payload!.CopySensitiveBytes();
        var changed = Rewrite(bytes, body =>
        {
            switch (change)
            {
                case "version": body["version"] = 99; break;
                case "version_type": body["version"] = "1"; break;
                case "api": body["api_mode"] = "messages"; break;
                case "native": body["history"] = JsonNode.Parse(History("Gemini.GenerateContent")); break;
                case "instructions_size": body["instructions"] = new string('x', LlmNativeContinuationCodec.MaximumInstructionCharacters + 1); break;
            }
        });
        try { Assert.Equal(expected, LlmNativeContinuationCodec.ImportSensitive(changed.AsSpan(), binding).Rejection); }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(changed); }
    }

    [Theory]
    [InlineData("openai", """[{"type":"reasoning","encrypted_content":""}]""")]
    [InlineData("anthropic", """[{"role":"assistant","content":[{"type":"thinking","thinking":"private-thought"}]}]""")]
    [InlineData("Gemini", """[{"type":"function_call","name":"write"}]""")]
    [InlineData("Gemini.GenerateContent", """[{"role":"model","parts":[{"functionCall":{"name":"write"}}]}]""")]
    [InlineData("ollama", """[{"role":"assistant","content":"answer","thinking":12}]""")]
    public void Source_carrier_must_match_the_adapters_native_boundary(string provider, string history)
    {
        using var source = new LlmNativeContinuation(provider, "fixture-model", Instructions, history);
        using var binding = Binding(provider);
        Assert.Equal(LlmNativeContinuationRejection.InvalidNativeShape,
            LlmNativeContinuationCodec.ExportSensitive(source, binding).Rejection);
    }

    [Fact]
    public void Size_depth_collection_and_ambiguous_json_are_bounded_before_import()
    {
        using var binding = Binding("openai");
        Assert.Equal(LlmNativeContinuationRejection.PayloadTooLarge,
            LlmNativeContinuationCodec.ImportSensitive(new byte[LlmNativeContinuationCodec.MaximumSerializedBytes + 1].AsSpan(), binding).Rejection);
        var deep = Encoding.UTF8.GetBytes(new string('[', LlmNativeContinuationCodec.MaximumDepth + 3) + "0" + new string(']', LlmNativeContinuationCodec.MaximumDepth + 3));
        Assert.Equal(LlmNativeContinuationRejection.DepthLimitExceeded,
            LlmNativeContinuationCodec.ImportSensitive(deep.AsSpan(), binding).Rejection);
        var many = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Repeat("{}", LlmNativeContinuationCodec.MaximumCollectionItems + 1)) + "]");
        Assert.Equal(LlmNativeContinuationRejection.CollectionLimitExceeded,
            LlmNativeContinuationCodec.ImportSensitive(many.AsSpan(), binding).Rejection);
        Assert.Equal(LlmNativeContinuationRejection.MalformedPayload,
            LlmNativeContinuationCodec.ImportSensitive("{\"payload\":{},\"payload\":{}}"u8, binding).Rejection);
        Assert.Equal(LlmNativeContinuationRejection.MalformedPayload,
            LlmNativeContinuationCodec.ImportSensitive("not-json"u8, binding).Rejection);
    }

    [Fact]
    public void Native_history_limits_apply_on_export_and_import_as_well_as_to_the_envelope()
    {
        var history = "[" + string.Join(",", Enumerable.Repeat("{\"role\":\"assistant\",\"content\":\"answer\"}",
            LlmNativeContinuationCodec.MaximumCollectionItems + 1)) + "]";
        using var largeCollection = new LlmNativeContinuation("ollama", "fixture-model", Instructions, history);
        using var ollama = Binding("ollama");
        Assert.Equal(LlmNativeContinuationRejection.CollectionLimitExceeded,
            LlmNativeContinuationCodec.ExportSensitive(largeCollection, ollama).Rejection);
        using var source = new LlmNativeContinuation("openai", "fixture-model", Instructions, History("openai"));
        using var binding = Binding("openai");
        var exported = LlmNativeContinuationCodec.ExportSensitive(source, binding);
        using var payload = exported.SensitivePayload;
        var bytes = payload!.CopySensitiveBytes();
        var changed = Rewrite(bytes, body => body["history"]![2]!["content"]![0]!["text"] = new string('x', LlmNativeContinuation.MaxPayloadCharacters + 1));
        try
        {
            Assert.Equal(LlmNativeContinuationRejection.PayloadTooLarge,
            LlmNativeContinuationCodec.ImportSensitive(changed.AsSpan(), binding).Rejection);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(changed); }
    }

    [Fact]
    public void Checksum_is_corruption_detection_while_host_authenticated_envelope_protects_integrity_and_scope()
    {
        using var binding = Binding("openai");
        using var source = new LlmNativeContinuation("openai", "fixture-model", Instructions, History("openai"));
        var exported = LlmNativeContinuationCodec.ExportSensitive(source, binding);
        using var payload = exported.SensitivePayload;
        var bytes = payload!.CopySensitiveBytes();
        var altered = Rewrite(bytes, body => body["history"]![2]!["content"]![0]!["text"] = "changed text", rehash: false);
        Assert.Equal(LlmNativeContinuationRejection.IntegrityMismatch,
            LlmNativeContinuationCodec.ImportSensitive(altered.AsSpan(), binding).Rejection);
        var unsignedForgery = Rewrite(bytes, body => body["history"]![2]!["content"]![0]!["text"] = "changed text");
        var untrusted = LlmNativeContinuationCodec.ImportSensitive(unsignedForgery.AsSpan(), binding);
        using var untrustedState = untrusted.Continuation;
        Assert.True(untrusted.Succeeded); // An unkeyed checksum is deliberately not an authenticity claim.
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[bytes.Length];
        var tag = new byte[16];
        var plain = new byte[bytes.Length];
        using var encryption = new AesGcm(key, tag.Length);
        try
        {
            encryption.Encrypt(nonce, bytes, ciphertext, tag, "conversation-alpha|scope|expiry"u8);
            Assert.Throws<AuthenticationTagMismatchException>(() => encryption.Decrypt(nonce, ciphertext, tag, plain, "conversation-beta|scope|expiry"u8));
            ciphertext[0] ^= 1;
            Assert.Throws<AuthenticationTagMismatchException>(() => encryption.Decrypt(nonce, ciphertext, tag, plain, "conversation-alpha|scope|expiry"u8));
        }
        finally
        {
            foreach (var array in new[] { bytes, altered, unsignedForgery, key, plain }) CryptographicOperations.ZeroMemory(array);
        }
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("xai")]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    [InlineData("Gemini")]
    [InlineData("Gemini.GenerateContent")]
    public async Task Persisted_capsule_reopens_in_a_fresh_provider_free_process(string provider)
    {
        using var source = new LlmNativeContinuation(provider, "fixture-model", Instructions, History(provider));
        using var binding = Binding(provider);
        var exported = LlmNativeContinuationCodec.ExportSensitive(source, binding);
        using var payload = exported.SensitivePayload;
        var bytes = payload!.CopySensitiveBytes();
        var root = RepositoryRoot();
        var directory = Path.Combine(root, ".agentica", "verification", "p05", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var capsulePath = Path.Combine(directory, "synthetic-capsule.json");
        var instructionPath = Path.Combine(directory, "synthetic-instructions.txt");
        await File.WriteAllBytesAsync(capsulePath, bytes);
        await File.WriteAllTextAsync(instructionPath, Instructions);
        var configuration = typeof(LlmNativeContinuationCodecTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var probe = Path.Combine(root, "eng", "NativeContinuationProbe", "bin", configuration, "net10.0", "NativeContinuationProbe.dll");
        Assert.True(File.Exists(probe), "The probe ProjectReference must build the fresh-process fixture.");
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var executable = string.IsNullOrWhiteSpace(dotnetRoot) ? "dotnet"
            : Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root
        };
        foreach (var argument in new[] { "exec", probe, capsulePath, instructionPath, provider, binding.ApiMode, binding.ModelId, Configuration })
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(directory, "cli-home");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await child.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, child.ExitCode);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), (await output).Trim());
            Assert.Equal("", await error);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            CryptographicOperations.ZeroMemory(bytes);
            File.Delete(capsulePath);
            File.Delete(instructionPath);
        }
    }

    private static LlmNativeContinuationBinding Binding(string provider, string configuration = Configuration) =>
        new(provider, provider switch
        {
            "openai" or "xai" => "responses",
            "anthropic" => "messages",
            "ollama" => "chat",
            "Gemini" => "interactions",
            "Gemini.GenerateContent" => "generate_content",
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        }, "fixture-model", configuration, Instructions);

    private static LlmConfigurationReport Report(string provider, LlmRequest request) => provider switch
    {
        "openai" => LlmConfigurationReports.Describe(request, new OpenAiResponsesClientOptions("fixture-key")),
        "xai" => LlmConfigurationReports.Describe(request, new XaiResponsesClientOptions("fixture-key")),
        "anthropic" => LlmConfigurationReports.Describe(request, new AnthropicMessagesClientOptions("fixture-key")),
        "ollama" => LlmConfigurationReports.Describe(request, new OllamaClientOptions()),
        "Gemini" => LlmConfigurationReports.Describe(request, new GeminiInteractionsClientOptions("fixture-key")),
        "Gemini.GenerateContent" => LlmConfigurationReports.Describe(request, new GeminiGenerateContentClientOptions("fixture-key")),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static JsonElement Body(string provider, LlmRequest request) => JsonSerializer.SerializeToElement(provider switch
    {
        "openai" => (object)new OpenAiResponsesLlmClient(new("fixture-key")).BuildRequestBody(request, "fixture-model"),
        "xai" => new XaiResponsesLlmClient(new("fixture-key")).BuildRequestBody(request, "fixture-model"),
        "anthropic" => AnthropicMessagesLlmClient.BuildRequestBody(request, "fixture-model"),
        "ollama" => OllamaLlmClient.BuildRequestBody(request, "fixture-model"),
        "Gemini" => GeminiInteractionsLlmClient.BuildRequestBody(request, "fixture-model"),
        "Gemini.GenerateContent" => GeminiGenerateContentLlmClient.BuildRequest(request, "fixture-model", out _),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    });

    private static byte[] Rewrite(byte[] source, Action<JsonObject> edit, bool rehash = true)
    {
        var envelope = JsonNode.Parse(source)!.AsObject();
        var body = envelope["payload"]!.AsObject();
        edit(body);
        if (rehash) envelope["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body.ToJsonString())));
        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "global.json")))
            directory = directory.Parent ?? throw new InvalidOperationException("Agentica repository root not found.");
        return directory.FullName;
    }

    private static string History(string provider) => provider switch
    {
        "openai" or "xai" => """[{"role":"user","content":"start"},{"type":"reasoning","encrypted_content":"opaque-openai","summary":[{"type":"summary_text","text":"private-thought"}],"provider_extra":{"keep":true}},{"type":"message","role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":"answer","annotations":[]}]}]""",
        "anthropic" => """[{"role":"user","content":"start"},{"role":"assistant","content":[{"type":"thinking","thinking":"private-thought","signature":"opaque-anthropic","provider_extra":true},{"type":"redacted_thinking","data":"opaque-redacted"},{"type":"text","text":"answer"}]}]""",
        "ollama" => """[{"role":"system","content":"private-policy-marker Δ 😀"},{"role":"user","content":"start"},{"role":"assistant","content":"answer","thinking":"private-thought","provider_extra":{"keep":true}}]""",
        "Gemini" => """[{"type":"user_input","content":[{"type":"text","text":"start"}]},{"type":"thought","signature":"opaque-gemini","provider_extra":{"keep":true}},{"type":"model_output","content":[{"type":"text","text":"answer"}],"provider_extra":true}]""",
        "Gemini.GenerateContent" => """[{"role":"user","parts":[{"text":"start"}]},{"role":"model","parts":[{"text":"private-thought","thought":true},{"text":"","thoughtSignature":"opaque-generate-content"},{"text":"answer"}]}]""",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
}
