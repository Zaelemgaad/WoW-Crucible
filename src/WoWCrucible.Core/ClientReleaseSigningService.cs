using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public sealed record ClientReleasePublisherKeyResult(
    string PrivateKeyPath,
    string PublicKeyPath,
    string KeyId,
    string Algorithm);

public sealed record ClientReleaseSignedChannelBody(
    string Format,
    int FormatVersion,
    string Channel,
    string ReleaseName,
    string ContentId,
    DateTimeOffset ReleaseCreatedUtc,
    DateTimeOffset PublishedUtc,
    string ManifestRelativePath,
    string ManifestSha256,
    string Changelog,
    int FileCount,
    long PayloadBytes,
    IReadOnlyList<string> OptionalGroups);

public sealed record ClientReleaseSignedChannel(
    string Format,
    int FormatVersion,
    string Algorithm,
    string KeyId,
    string BodyBase64,
    string SignatureBase64);

public sealed record ClientReleaseSignedChannelInspection(
    string SignedChannelPath,
    string SignedChannelSha256,
    string TrustedPublicKeyPath,
    string TrustedPublicKeySha256,
    string KeyId,
    string BundleRoot,
    string ManifestPath,
    ClientReleaseManifest Manifest,
    ClientReleaseSignedChannelBody Body);

public sealed record ClientReleaseTrustBinding(
    string SignedChannelPath,
    string SignedChannelSha256,
    string TrustedPublicKeyPath,
    string TrustedPublicKeySha256,
    string KeyId,
    string Algorithm);

/// <summary>
/// Creates offline publisher identities and authenticates immutable client release bundles.
/// The exact body bytes are carried as base64 inside the signed envelope, avoiding any
/// dependence on implementation-specific JSON canonicalization during verification.
/// </summary>
public static class ClientReleaseSigningService
{
    public const string SignedChannelFormat = "wow-crucible-client-release-channel";
    public const string SignedBodyFormat = "wow-crucible-client-release-channel-body";
    public const string Algorithm = "ECDSA-P256-SHA256";
    public const int FormatVersion = 1;
    public const int MinimumPasswordLength = 12;
    private const int Pbkdf2Iterations = 210_000;
    private const int MaximumArtifactBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly JsonSerializerOptions PrettyJson = new(StrictJson) { WriteIndented = true };

    public static ClientReleasePublisherKeyResult CreatePublisherKey(
        string privateKeyPath,
        string publicKeyPath,
        ReadOnlySpan<char> password)
    {
        RequirePassword(password);
        privateKeyPath = PrepareNewOutput(privateKeyPath);
        publicKeyPath = PrepareNewOutput(publicKeyPath);
        if (privateKeyPath.Equals(publicKeyPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Publisher private and public key paths must be different.");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privatePem = key.ExportEncryptedPkcs8PrivateKeyPem(password,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Pbkdf2Iterations));
        var publicPem = key.ExportSubjectPublicKeyInfoPem();
        var keyId = KeyId(key);

        var privateTemp = TemporarySibling(privateKeyPath);
        var publicTemp = TemporarySibling(publicKeyPath);
        try
        {
            File.WriteAllText(privateTemp, privatePem, new UTF8Encoding(false));
            File.WriteAllText(publicTemp, publicPem, new UTF8Encoding(false));
            using (var privateCheck = LoadPrivateKey(privateTemp, password))
                if (!KeyId(privateCheck).Equals(keyId, StringComparison.Ordinal)) throw new CryptographicException("Encrypted publisher private-key verification failed.");
            using (var publicCheck = LoadPublicKey(publicTemp))
                if (!KeyId(publicCheck).Equals(keyId, StringComparison.Ordinal)) throw new CryptographicException("Publisher public-key verification failed.");

            File.Move(privateTemp, privateKeyPath, false);
            try { File.Move(publicTemp, publicKeyPath, false); }
            catch { File.Delete(privateKeyPath); throw; }
            return new(privateKeyPath, publicKeyPath, keyId, Algorithm);
        }
        finally
        {
            if (File.Exists(privateTemp)) File.Delete(privateTemp);
            if (File.Exists(publicTemp)) File.Delete(publicTemp);
        }
    }

    public static ClientReleaseSignedChannelInspection SignBundle(
        string manifestOrBundlePath,
        string encryptedPrivateKeyPath,
        ReadOnlySpan<char> password,
        string signedChannelPath)
    {
        RequirePassword(password);
        var manifestPath = ResolveManifest(manifestOrBundlePath);
        var bundleRoot = Path.GetDirectoryName(manifestPath) ?? throw new InvalidOperationException("Release manifest has no parent folder.");
        signedChannelPath = Path.GetFullPath(signedChannelPath);
        if (File.Exists(signedChannelPath) || Directory.Exists(signedChannelPath)) throw new IOException($"Output already exists: {signedChannelPath}");
        if (!Path.GetDirectoryName(signedChannelPath)!.Equals(bundleRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The signed channel descriptor must be created beside release.crucible.json so the bundle remains portable.");
        if (Path.GetFileName(signedChannelPath).Equals(ClientReleaseService.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The signed channel descriptor cannot replace the release manifest.");

        encryptedPrivateKeyPath = RequireFile(encryptedPrivateKeyPath, "Encrypted publisher private key");
        if (IsInside(encryptedPrivateKeyPath, bundleRoot))
            throw new InvalidOperationException("A publisher private key must never be stored inside its release bundle.");

        var manifest = ClientReleaseService.LoadManifest(manifestPath);
        ClientReleaseService.VerifyBundle(bundleRoot, manifest);
        var body = new ClientReleaseSignedChannelBody(
            SignedBodyFormat,
            FormatVersion,
            manifest.Channel,
            manifest.Name,
            manifest.ContentId,
            manifest.CreatedUtc,
            DateTimeOffset.UtcNow,
            ClientReleaseService.ManifestFileName,
            Sha256File(manifestPath),
            manifest.Changelog,
            manifest.Files.Count,
            manifest.Files.Sum(file => file.Length),
            OptionalGroups(manifest));
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body, StrictJson);

        using var privateKey = LoadPrivateKey(encryptedPrivateKeyPath, password);
        var keyId = KeyId(privateKey);
        var signature = privateKey.SignData(bodyBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var envelope = new ClientReleaseSignedChannel(
            SignedChannelFormat,
            FormatVersion,
            Algorithm,
            keyId,
            Convert.ToBase64String(bodyBytes),
            Convert.ToBase64String(signature));
        WriteNew(signedChannelPath, envelope);

        // Re-read the artifact from disk, then verify with the derived public key. This
        // catches serialization/write mistakes without writing or logging secret material.
        // The caller still distributes a separately exported public key as the trust anchor.
        var written = ReadStrict<ClientReleaseSignedChannel>(signedChannelPath);
        var writtenBody = Convert.FromBase64String(written.BodyBase64);
        var writtenSignature = Convert.FromBase64String(written.SignatureBase64);
        if (written != envelope || !writtenBody.AsSpan().SequenceEqual(bodyBytes) ||
            !privateKey.VerifyData(writtenBody, writtenSignature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            File.Delete(signedChannelPath);
            throw new CryptographicException("The newly written release signature failed immediate verification.");
        }
        return new(signedChannelPath, Sha256File(signedChannelPath), string.Empty, string.Empty, keyId, bundleRoot, manifestPath, manifest, body);
    }

    public static ClientReleaseSignedChannelInspection VerifySignedChannel(
        string signedChannelPath,
        string trustedPublicKeyPath,
        IProgress<ClientReleaseProgress>? progress = null)
    {
        signedChannelPath = RequireFile(signedChannelPath, "Signed channel descriptor");
        trustedPublicKeyPath = RequireFile(trustedPublicKeyPath, "Trusted publisher public key");
        var signedChannelBytes = ReadBoundedBytes(signedChannelPath, "Signed channel descriptor");
        var envelope = DeserializeStrict<ClientReleaseSignedChannel>(signedChannelBytes, signedChannelPath);
        if (!envelope.Format.Equals(SignedChannelFormat, StringComparison.Ordinal) || envelope.FormatVersion != FormatVersion)
            throw new InvalidDataException("Unsupported signed release channel format.");
        if (!envelope.Algorithm.Equals(Algorithm, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported release signature algorithm: {envelope.Algorithm}");
        if (!IsSha256(envelope.KeyId)) throw new InvalidDataException("Signed release channel has an invalid publisher key ID.");

        byte[] bodyBytes;
        byte[] signature;
        try
        {
            bodyBytes = Convert.FromBase64String(envelope.BodyBase64);
            signature = Convert.FromBase64String(envelope.SignatureBase64);
        }
        catch (FormatException exception) { throw new InvalidDataException("Signed release channel contains invalid base64 data.", exception); }
        if (bodyBytes.Length is 0 or > MaximumArtifactBytes || signature.Length != 64)
            throw new InvalidDataException("Signed release channel body or P-256 signature has an invalid length.");

        var trustedPublicKeyBytes = ReadBoundedBytes(trustedPublicKeyPath, "Trusted publisher public key");
        using var publicKey = LoadPublicKey(trustedPublicKeyBytes);
        var trustedKeyId = KeyId(publicKey);
        if (!FixedHashEquals(envelope.KeyId, trustedKeyId))
            throw new CryptographicException($"Signed release publisher {envelope.KeyId} does not match trusted key {trustedKeyId}.");
        if (!publicKey.VerifyData(bodyBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Release channel signature is invalid. The descriptor was changed or came from another publisher.");

        var body = DeserializeStrict<ClientReleaseSignedChannelBody>(bodyBytes, "Signed release channel body");
        ValidateBody(body);
        var bundleRoot = Path.GetDirectoryName(signedChannelPath) ?? throw new InvalidDataException("Signed channel descriptor has no parent folder.");
        var manifestPath = Path.Combine(bundleRoot, body.ManifestRelativePath);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("The signed release manifest is missing beside its channel descriptor.", manifestPath);
        if (!FixedHashEquals(body.ManifestSha256, Sha256File(manifestPath)))
            throw new CryptographicException("The release manifest does not match the publisher-signed SHA-256 identity.");

        var manifest = ClientReleaseService.LoadManifest(manifestPath);
        if (!FixedHashEquals(body.ManifestSha256, Sha256File(manifestPath)))
            throw new CryptographicException("The release manifest changed while its signed identity was being verified.");
        ValidateManifestBinding(body, manifest);
        ClientReleaseService.VerifyBundle(bundleRoot, manifest, progress);
        return new(
            signedChannelPath,
            Convert.ToHexString(SHA256.HashData(signedChannelBytes)),
            trustedPublicKeyPath,
            Convert.ToHexString(SHA256.HashData(trustedPublicKeyBytes)),
            trustedKeyId,
            bundleRoot,
            manifestPath,
            manifest,
            body);
    }

    internal static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ValidateBody(ClientReleaseSignedChannelBody body)
    {
        if (!body.Format.Equals(SignedBodyFormat, StringComparison.Ordinal) || body.FormatVersion != FormatVersion)
            throw new InvalidDataException("Unsupported signed release channel body format.");
        if (!body.ManifestRelativePath.Equals(ClientReleaseService.ManifestFileName, StringComparison.Ordinal))
            throw new InvalidDataException("Signed release channel v1 must reference release.crucible.json beside itself.");
        if (!IsSha256(body.ManifestSha256) || !IsSha256(body.ContentId))
            throw new InvalidDataException("Signed release channel contains an invalid manifest or content identity.");
        if (string.IsNullOrWhiteSpace(body.Channel) || string.IsNullOrWhiteSpace(body.ReleaseName) || body.FileCount <= 0 || body.PayloadBytes < 0)
            throw new InvalidDataException("Signed release channel contains incomplete release metadata.");
        if (body.OptionalGroups.Any(string.IsNullOrWhiteSpace) || body.OptionalGroups.Distinct(StringComparer.OrdinalIgnoreCase).Count() != body.OptionalGroups.Count ||
            !body.OptionalGroups.SequenceEqual(body.OptionalGroups.Order(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal))
            throw new InvalidDataException("Signed release optional groups are not unique and canonically ordered.");
    }

    private static void ValidateManifestBinding(ClientReleaseSignedChannelBody body, ClientReleaseManifest manifest)
    {
        if (!body.Channel.Equals(manifest.Channel, StringComparison.Ordinal) ||
            !body.ReleaseName.Equals(manifest.Name, StringComparison.Ordinal) ||
            !FixedHashEquals(body.ContentId, manifest.ContentId) ||
            body.ReleaseCreatedUtc != manifest.CreatedUtc ||
            !body.Changelog.Equals(manifest.Changelog, StringComparison.Ordinal) ||
            body.FileCount != manifest.Files.Count ||
            body.PayloadBytes != manifest.Files.Sum(file => file.Length) ||
            !body.OptionalGroups.SequenceEqual(OptionalGroups(manifest), StringComparer.Ordinal))
            throw new CryptographicException("The release manifest metadata does not match the signed channel body.");
    }

    private static IReadOnlyList<string> OptionalGroups(ClientReleaseManifest manifest)
        => manifest.Files.Where(file => file.OptionalGroup is not null).Select(file => file.OptionalGroup!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static ECDsa LoadPrivateKey(string path, ReadOnlySpan<char> password)
    {
        var pem = ReadBoundedText(path);
        if (!pem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
            throw new CryptographicException("Publisher private keys must use password-encrypted PKCS#8 PEM.");
        var key = ECDsa.Create();
        try { key.ImportFromEncryptedPem(pem, password); return key; }
        catch { key.Dispose(); throw; }
    }

    private static ECDsa LoadPublicKey(string path)
    {
        return LoadPublicKey(ReadBoundedBytes(path, "Publisher public key"));
    }

    private static ECDsa LoadPublicKey(ReadOnlySpan<byte> bytes)
    {
        var pem = Encoding.UTF8.GetString(bytes);
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new CryptographicException("The trusted-key input must be a public key, never a private key.");
        var key = ECDsa.Create();
        try { key.ImportFromPem(pem); return key; }
        catch { key.Dispose(); throw; }
    }

    private static string KeyId(ECDsa key) => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

    private static void RequirePassword(ReadOnlySpan<char> password)
    {
        if (password.Length < MinimumPasswordLength)
            throw new ArgumentException($"Publisher key password must contain at least {MinimumPasswordLength} characters.");
    }

    private static T ReadStrict<T>(string path)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumArtifactBytes) throw new InvalidDataException($"JSON artifact has an invalid length: {path}");
        return DeserializeStrict<T>(File.ReadAllBytes(path), path);
    }

    private static T DeserializeStrict<T>(ReadOnlySpan<byte> bytes, string label)
    {
        try { return JsonSerializer.Deserialize<T>(bytes, StrictJson) ?? throw new InvalidDataException($"{label} is empty."); }
        catch (JsonException exception) { throw new InvalidDataException($"{label} is invalid: {exception.Message}", exception); }
    }

    private static string ReadBoundedText(string path)
    {
        return Encoding.UTF8.GetString(ReadBoundedBytes(path, "Key file"));
    }

    private static byte[] ReadBoundedBytes(string path, string label)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumArtifactBytes) throw new InvalidDataException($"{label} has an invalid length: {path}");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != info.Length) throw new IOException($"{label} changed while it was being read: {path}");
        return bytes;
    }

    private static string ResolveManifest(string path)
    {
        path = Path.GetFullPath(path);
        var manifest = Directory.Exists(path) ? Path.Combine(path, ClientReleaseService.ManifestFileName) : path;
        return RequireFile(manifest, "Client release manifest");
    }

    private static string RequireFile(string path, string label)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException($"{label} does not exist.", path);
        return path;
    }

    private static string PrepareNewOutput(string path)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException($"Output already exists: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent folder."));
        return path;
    }

    private static string TemporarySibling(string path) => path + $".{Guid.NewGuid():N}.tmp";

    private static void WriteNew<T>(string path, T value)
    {
        var temporary = TemporarySibling(path);
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, PrettyJson), new UTF8Encoding(false));
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsInside(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return full.Equals(parent, StringComparison.OrdinalIgnoreCase) || full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool FixedHashEquals(string left, string right)
    {
        if (!IsSha256(left) || !IsSha256(right)) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    }
}
