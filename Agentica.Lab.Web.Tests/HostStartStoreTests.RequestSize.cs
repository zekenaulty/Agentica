using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HostStartStoreTests
{
    [Theory]
    [InlineData('m')]
    [InlineData('+')]
    [InlineData('\u00e9')]
    public void Exactly65536SerializedBytesPerRequestSurviveAdmissionAndReload(char padding)
    {
        var store = new HostStartStore(_root);
        var request = RequestSized(Request(), HostStartStore.MaximumRequestBytes, padding);
        Assert.Equal(65_536, RequestBytes(request));
        var entry = store.Admit("attempt", request, request, "run");
        Same(entry, new HostStartStore(_root).Find(Key())!);
        var before = Bytes();
        Same(entry, store.Admit("attempt", request, request, "run"));
        Assert.Equal(before, Bytes());
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    [InlineData(true, 262144)]
    [InlineData(false, 262144)]
    public void EitherOversizedRequestFailsWithStartSpecificCodeBeforeWriting(bool oversizedOriginal, int extraBytes)
    {
        var store = new HostStartStore(_root);
        var (original, accepted) = SizePair(oversizedOriginal, HostStartStore.MaximumRequestBytes + extraBytes);
        Assert.True(RequestBytes(oversizedOriginal ? accepted : original) < HostStartStore.MaximumRequestBytes);
        var before = Bytes();
        Error("start.request_size", () => store.Admit("attempt", original, accepted, "run"));
        Assert.Equal(before, Bytes());
        Assert.Null(store.Find(Key()));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData('+')]
    [InlineData('\u00e9')]
    public void SerializedEscapingCountsTowardTheCapRatherThanCharacterLength(char padding)
    {
        var store = new HostStartStore(_root);
        var request = RequestSized(Request(), HostStartStore.MaximumRequestBytes + 1, padding);
        Assert.True(request.Provider!.Model!.Length < HostStartStore.MaximumRequestBytes);
        // Legacy validation keeps its existing 262144-byte limit.
        ProtocolValidation.Validate(request);
        Error("start.request_size", () => store.Admit("attempt", request, request, "run"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OversizedRetainedRequestsFailClosedEvenWithValidEntryAndEnvelopeHashes(bool oversizedOriginal)
    {
        var store = new HostStartStore(_root);
        var entry = Admit(store);
        var (original, accepted) = SizePair(oversizedOriginal, HostStartStore.MaximumRequestBytes + 1);
        ProtocolValidation.Validate(original);
        ProtocolValidation.Validate(accepted);
        entry = entry with { OriginalRequest = original, AcceptedRequest = accepted,
            OriginalRequestHash = ProtocolValidation.Digest(original), AcceptedRequestHash = ProtocolValidation.Digest(accepted) };
        File.WriteAllBytes(LedgerPath, LedgerBytes([entry]));
        var before = Bytes();
        var error = Assert.Throws<InvalidDataException>(() => new HostStartStore(_root));
        Assert.Equal("start.request_size", Assert.IsType<HostProtocolException>(error.InnerException).Code);
        Assert.Throws<InvalidDataException>(() => store.Find(Key()));
        Assert.Throws<InvalidDataException>(() => store.HasOpen("host", "session"));
        Assert.Throws<InvalidDataException>(() => store.RequestStop(Reference(entry)));
        Assert.Equal(before, Bytes());
    }

    private static (HostRunRequest Original, HostRunRequest Accepted) SizePair(bool oversizedOriginal, int bytes)
    {
        var request = Request();
        if (oversizedOriginal)
        {
            var original = request with { Provider = request.Provider! with
                { Provider = "openai" + new string(' ', bytes - RequestBytes(request)) } };
            Assert.Equal(bytes, RequestBytes(original));
            return (original, request);
        }
        var unspecified = request with { Provider = request.Provider! with { Model = null } };
        var accepted = RequestSized(unspecified with { Provider = unspecified.Provider! with { ThinkingEffortResolved = true } }, bytes);
        return (unspecified, accepted);
    }

    private static int RequestBytes(HostRunRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, HostProtocol.Json).Length;
    private static HostRunRequest RequestSized(HostRunRequest request, int bytes, char padding = 'm')
    {
        request = request with { Provider = request.Provider! with { Model = "" } };
        var remaining = bytes - RequestBytes(request);
        Assert.True(remaining >= 0);
        var unitBytes = JsonSerializer.SerializeToUtf8Bytes(padding.ToString(), HostProtocol.Json).Length - 2;
        return request with { Provider = request.Provider with
            { Model = new string(padding, remaining / unitBytes) + new string('m', remaining % unitBytes) } };
    }
}
