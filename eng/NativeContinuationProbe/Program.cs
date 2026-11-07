using System.Security.Cryptography;
using Agentica.Clients.Llm;

// Provider-free fresh-process fixture. Paths contain synthetic test data, never actual credentials.
// Output is a digest or stable rejection; sensitive content is never printed.
if (args.Length != 6) return 2;
var bytes = File.ReadAllBytes(args[0]);
try
{
    using var binding = new LlmNativeContinuationBinding(args[2], args[3], args[4], args[5], File.ReadAllText(args[1]));
    var imported = LlmNativeContinuationCodec.ImportSensitive(bytes.AsSpan(), binding);
    if (!imported.Succeeded) { Console.WriteLine(imported.Rejection); return 3; }
    using var continuation = imported.Continuation;
    var check = LlmNativeContinuationCodec.ValidateBinding(continuation, binding);
    if (!check.IsCompatible || !check.ConfigurationBound) return 4;
    var exported = LlmNativeContinuationCodec.ExportSensitive(continuation, binding);
    using var payload = exported.SensitivePayload;
    if (!exported.Succeeded) { Console.WriteLine(exported.Rejection); return 5; }
    var reopened = payload!.CopySensitiveBytes();
    try { Console.WriteLine(Convert.ToHexStringLower(SHA256.HashData(reopened))); }
    finally { CryptographicOperations.ZeroMemory(reopened); }
    return 0;
}
finally { CryptographicOperations.ZeroMemory(bytes); }
