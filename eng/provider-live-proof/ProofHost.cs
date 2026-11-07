using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Agentica.Lab.Web;
using Agentica.Lab.Web.Chat;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Agentica.ProviderLiveProof;

/// <summary>Explicitly invoked, isolated Lab host. Building or self-testing this driver
/// never calls this entry point, reads a credential, starts a host or submits a turn.</summary>
internal static class ProofHost
{
    internal static async Task<int> RunAsync(string mode, string stateRoot, int port,
        string labContentRoot, CancellationToken cancellationToken)
    {
        if (mode is not ("serve" or "run") || port is < 1024 or > 65535)
            throw new ArgumentException("proof.invalid_host_options");
        stateRoot = Path.GetFullPath(stateRoot);
        labContentRoot = Path.GetFullPath(labContentRoot);
        if (!Directory.Exists(Path.Combine(labContentRoot, "wwwroot")))
            throw new ArgumentException("proof.lab_content_root_missing");
        Directory.CreateDirectory(stateRoot);
        var address = new Uri($"http://127.0.0.1:{port}");
        var scenarioPath = Path.Combine(stateRoot, "scenario.json");
        var evidencePath = Path.Combine(stateRoot, "scenario-evidence.json");
        ScenarioVerification? beforeRestart = null;
        ProofScenario? scenario = null;
        string? blocked = null;
        int physicalAttempts;
        decimal held;
        string ledgerHash;

        await using (var session = await StartAsync(stateRoot, address, labContentRoot, cancellationToken).ConfigureAwait(false))
        {
            if (mode == "serve")
            {
                Console.WriteLine($"Isolated proof Lab: {address}/chat.html");
                Console.WriteLine("No turn was submitted. The cumulative proof ledger remains in force.");
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                return 0;
            }

            var store = session.App.Services.GetRequiredService<ExperimentStore>();
            using var api = Api(address);
            if (File.Exists(scenarioPath))
            {
                scenario = JsonSerializer.Deserialize<ProofScenario>(await File.ReadAllBytesAsync(scenarioPath, cancellationToken).ConfigureAwait(false), HostProtocol.Json)
                    ?? throw new InvalidDataException("proof.invalid_scenario");
                scenario.Validate();
                // A saved intent never authorizes another submission. Reconcile the
                // existing request, including a crash between submission and saving its ID.
                var existing = scenario.TurnId is not null ? store.GetTurn(scenario.ChatId, scenario.TurnId)
                    : store.FindRequest(scenario.ChatId, scenario.RequestId, ScenarioRunner.Prompt);
                if (existing is null) blocked = "proof.saved_intent_has_no_observed_turn_no_resend";
                else
                {
                    scenario = scenario with { TurnId = existing.Id };
                    Save(scenarioPath, scenario);
                    if (existing.Status == "running") blocked = "proof.existing_turn_unresolved_no_resend";
                    else beforeRestart = ScenarioRunner.Verify(store, scenario, session.Guard.Snapshot());
                }
            }
            else
            {
                using var createdResponse = await api.PostAsJsonAsync("/api/chats", new ExperimentCreateRequest("P09 bounded live loop proof"), HostProtocol.Json, cancellationToken).ConfigureAwait(false);
                var created = await ReadAsync<ExperimentConversation>(createdResponse, cancellationToken).ConfigureAwait(false);
                using var settingsResponse = await api.PutAsJsonAsync($"/api/chats/{created.Id}/settings",
                    new ExperimentSettingsUpdate(created.SettingsRevision, ScenarioRunner.Settings()), HostProtocol.Json, cancellationToken).ConfigureAwait(false);
                _ = await ReadAsync<ExperimentConversation>(settingsResponse, cancellationToken).ConfigureAwait(false);
                await SeedChallengeAsync(store, created.Id, cancellationToken).ConfigureAwait(false);
                scenario = new(created.Id, "proof-" + Guid.NewGuid().ToString("N"));
                // Persist custody before submitting. No marker, operand or expected
                // answer enters this manifest or the user prompt.
                Save(scenarioPath, scenario, createNew: true);
                try
                {
                    using var sentResponse = await api.PostAsJsonAsync($"/api/chats/{created.Id}/turns",
                        new ExperimentSendRequest(ScenarioRunner.Prompt, scenario.RequestId), HostProtocol.Json, cancellationToken).ConfigureAwait(false);
                    var turn = await ReadAsync<ExperimentTurn>(sentResponse, cancellationToken).ConfigureAwait(false);
                    scenario = scenario with { TurnId = turn.Id };
                    Save(scenarioPath, scenario);
                    do
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        turn = store.GetTurn(scenario.ChatId, turn.Id);
                        if (turn.Status != "running") break;
                        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                    } while (true);
                    beforeRestart = ScenarioRunner.Verify(store, scenario, session.Guard.Snapshot());
                }
                catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException or OperationCanceledException))
                {
                    // The API may have admitted a turn before delivery failed. Do not
                    // retry the request or erase its intent; inspect on the next invocation.
                    blocked = "proof.submission_or_verification_unavailable_no_resend";
                }
            }
            physicalAttempts = session.Guard.Snapshot().Attempts.Count;
            held = session.Guard.HeldUsd;
            ledgerHash = HashLedger(session.Guard.Snapshot());
        }

        if (scenario is null || beforeRestart is null)
        {
            Save(evidencePath, new
            {
                contract = "agentica.live-proof.scenario.v1",
                accepted = false,
                blocked,
                scenario?.ChatId,
                scenario?.TurnId,
                physicalAttempts,
                heldUsd = held,
                ledgerHash,
                automaticallyResubmitted = false
            });
            return 2;
        }

        // Recreate the host against precisely the same Lab store and cumulative
        // provider ledger. Reopen performs reads only; no second turn is submitted.
        ScenarioVerification afterRestart;
        await using (var reopened = await StartAsync(stateRoot, address, labContentRoot, cancellationToken).ConfigureAwait(false))
        {
            var store = reopened.App.Services.GetRequiredService<ExperimentStore>();
            using var api = Api(address);
            using var response = await api.GetAsync($"/api/chats/{scenario.ChatId}/turns/{scenario.TurnId}", cancellationToken).ConfigureAwait(false);
            var publicTurn = await ReadAsync<ExperimentTurn>(response, cancellationToken).ConfigureAwait(false);
            afterRestart = ScenarioRunner.Verify(store, scenario, reopened.Guard.Snapshot());
            if (publicTurn.AssistantText != store.GetTurn(scenario.ChatId, scenario.TurnId!).AssistantText ||
                reopened.Guard.Snapshot().Attempts.Count != physicalAttempts || reopened.Guard.HeldUsd != held ||
                HashLedger(reopened.Guard.Snapshot()) != ledgerHash)
                throw new InvalidDataException("proof.reopen_changed_provider_custody");
        }
        var stable = beforeRestart == afterRestart;
        Save(evidencePath, new
        {
            contract = "agentica.live-proof.scenario.v1",
            accepted = stable && beforeRestart.Accepted,
            restartVerified = stable,
            automaticallyResubmitted = false,
            providerCallsDuringReopen = 0,
            physicalAttempts,
            heldUsd = held,
            ledgerHash,
            scenario.ChatId,
            scenario.TurnId,
            verification = afterRestart
        });
        Console.WriteLine(stable && beforeRestart.Accepted ? "Proof accepted; saved chat and evidence survived a read-only host reopen."
            : "Proof did not satisfy all acceptance checks. No automatic retry was made.");
        return stable && beforeRestart.Accepted ? 0 : 3;
    }

    private static async Task<HostSession> StartAsync(string stateRoot, Uri address, string contentRoot, CancellationToken cancellationToken)
    {
        var guard = new DurableProviderGuard(Path.Combine(stateRoot, "provider-ledger.json"),
            new SocketsHttpHandler { AllowAutoRedirect = false });
        var guardedHttp = new HttpClient(guard) { Timeout = Timeout.InfiniteTimeSpan };
        var configuration = new OpenAiProviderConfiguration(name => name == "OPENAI_API_KEY"
            ? Environment.GetEnvironmentVariable("OPENAI_REPLIKA_KEY") : null);
        IExperimentExecutor executor = new LinkedCountExecutor(new OpenAiExperimentExecutor(configuration, guardedHttp), guard);
        WebApplication? app = null;
        try
        {
            app = LabWebApplication.Create(["--urls", address.AbsoluteUri, "--contentRoot", contentRoot],
                storageDirectory: Path.Combine(stateRoot, "lab"), openAiConfiguration: configuration, experimentExecutor: executor);
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            return new(app, guardedHttp, guard, executor);
        }
        catch
        {
            if (app is not null) await app.DisposeAsync().ConfigureAwait(false);
            (executor as IDisposable)?.Dispose();
            guardedHttp.Dispose();
            throw;
        }
    }

    private static HttpClient Api(Uri address) => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    { BaseAddress = address, Timeout = Timeout.InfiniteTimeSpan };

    private static string HashLedger(ProviderLedger ledger) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ledger, HostProtocol.Json)));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("proof.lab_api_rejected");
        return await response.Content.ReadFromJsonAsync<T>(HostProtocol.Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("proof.lab_api_invalid_response");
    }

    private static async Task SeedChallengeAsync(ExperimentStore store, string chatId, CancellationToken cancellationToken)
    {
        var scratch = Path.Combine(store.WorkspaceRoot, chatId);
        Directory.CreateDirectory(scratch);
        var challenge = new ProofChallenge(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            RandomNumberGenerator.GetInt32(1000, 10000), RandomNumberGenerator.GetInt32(1000, 10000));
        await using var file = new FileStream(Path.Combine(scratch, ScenarioRunner.ChallengeFile), FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(file, challenge, HostProtocol.Json, cancellationToken).ConfigureAwait(false);
    }

    private static void Save<T>(string path, T value, bool createNew = false)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, HostProtocol.Json);
        if (createNew)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return;
        }
        var temporary = path + ".next";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        File.Move(temporary, path, overwrite: true);
    }

    private sealed class HostSession(WebApplication app, HttpClient http, DurableProviderGuard guard, IExperimentExecutor executor) : IAsyncDisposable
    {
        internal WebApplication App => app;
        internal DurableProviderGuard Guard => guard;
        public async ValueTask DisposeAsync()
        {
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await app.StopAsync(stop.Token).ConfigureAwait(false);
            }
            finally
            {
                await app.DisposeAsync().ConfigureAwait(false);
                (executor as IDisposable)?.Dispose();
                http.Dispose();
            }
        }
    }
}

internal sealed record ProofScenario(string ChatId, string RequestId, string? TurnId = null)
{
    internal void Validate()
    {
        if (!Guid.TryParseExact(ChatId, "N", out _) || RequestId.Length is < 1 or > 128 ||
            RequestId.Any(char.IsControl) || TurnId is not null && !Guid.TryParseExact(TurnId, "N", out _))
            throw new InvalidDataException("proof.invalid_scenario");
    }
}

internal sealed record ProofChallenge(string Marker, int Left, int Right);
