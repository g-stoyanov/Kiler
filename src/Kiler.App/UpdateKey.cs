namespace Kiler.App;

/// <summary>
/// The public key of the releases (M3 step 11, M3.5; docs/release.md): the base64 of the SubjectPublicKeyInfo of the
/// maintainer's signing key of ECDSA P-256, made with <c>keygen</c> of the release tool on 2026-11-11. The local copies check
/// the signature of <c>version.json</c> with it (M3.6, M3.8), never with a key from the shared folder. Its private key is
/// only in the key file and the password manager. It is never a key of the tests (<c>UpdateKeyTests</c>), and Kiler.App never
/// uses <c>SigningKey</c>. How a release brings a new key is decided in M3.6 (docs/release.md, "The signing key").
/// </summary>
internal static class UpdateKey
{
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEbgyju0TgBqpKmwFqdrVpxrDauBKtYxalv71MoWdMrDfPuoVCS81469YbqZs2eExvz3o3IxGIF50lBTWIs3AViw==";
}
