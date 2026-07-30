using System.Runtime.InteropServices;

namespace CouchbaseInsight.Services;

/// <summary>
/// <see cref="ICredentialStore"/> backed by the Windows Credential Manager (the OS secret vault,
/// the same store Git Credential Manager / GitHub Desktop use). Secrets are stored as generic
/// credentials, encrypted per-user by the OS, and are auditable via Control Panel → Credential
/// Manager. Implemented with direct P/Invoke so no third-party dependency touches the secret path.
/// </summary>
public class WindowsCredentialStore : ICredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    public void Write(string target, string username, string secret)
    {
        var blob = IntPtr.Zero;
        try
        {
            var blobSize = 0;
            if (!string.IsNullOrEmpty(secret))
            {
                blob = Marshal.StringToHGlobalUni(secret);
                blobSize = secret.Length * sizeof(char); // UTF-16 bytes, excluding terminator
            }

            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = username,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)blobSize,
                Persist = CRED_PERSIST_LOCAL_MACHINE
            };

            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException(
                    $"Failed to store credential '{target}' (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            if (blob != IntPtr.Zero) Marshal.FreeHGlobal(blob);
        }
    }

    public string? ReadSecret(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            if (Marshal.GetLastWin32Error() == ERROR_NOT_FOUND) return null;
            return null; // any other read failure: treat as absent rather than crashing the load
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                return string.Empty;

            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)(cred.CredentialBlobSize / sizeof(char)));
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public void Delete(string target)
    {
        if (!CredDelete(target, CRED_TYPE_GENERIC, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ERROR_NOT_FOUND)
                throw new InvalidOperationException(
                    $"Failed to delete credential '{target}' (Win32 error {err}).");
        }
    }

    public IReadOnlyList<string> Enumerate(string prefix)
    {
        if (!CredEnumerate(prefix + "*", 0, out var count, out var credsPtr))
        {
            if (Marshal.GetLastWin32Error() == ERROR_NOT_FOUND) return Array.Empty<string>();
            return Array.Empty<string>();
        }

        try
        {
            var targets = new List<string>((int)count);
            for (var i = 0; i < count; i++)
            {
                var entryPtr = Marshal.ReadIntPtr(credsPtr, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<CREDENTIAL>(entryPtr);
                if (!string.IsNullOrEmpty(cred.TargetName))
                    targets.Add(cred.TargetName);
            }
            return targets;
        }
        finally
        {
            CredFree(credsPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerate(string? filter, uint flags, out uint count, out IntPtr credentialsPtr);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
