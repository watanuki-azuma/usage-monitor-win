// Windows Credential Manager reader (milestone M3).
//
// Port of reference/js/wincred.js. The Electron app shells out to PowerShell and compiles the
// P/Invoke there; the native app calls the same two advapi32 entry points in-process (SPEC 2),
// which is the reason this file exists at all.
//
// Only CRED_TYPE_GENERIC (1) credentials are read, the blob is decoded as UTF-8, and anything
// that prevents a read (target absent, empty blob, marshalling failure) comes back as null so
// the adapters can fall back to the on-disk token files.
//
// Language level: C# 5 (in-box csc.exe). No WinForms here.
//
// NOTE: this talks to the *real* credential store, so tests inject their own ICredentialReader
// (FakeCred) and this class is never instantiated from the test suite.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace UsageMonitor
{
    public class WinCredReader : ICredentialReader
    {
        private const int CredTypeGeneric = 1;

        // CREDENTIALW, exactly as advapi32 lays it out (matches the C# used by the old
        // PowerShell script in wincred.js).
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public long LastWritten;            // FILETIME
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
        private static extern void CredFree(IntPtr cred);

        public string ReadGenericUtf8(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;

            IntPtr ptr;
            try
            {
                if (!CredRead(target, CredTypeGeneric, 0, out ptr)) return null;   // ERROR_NOT_FOUND and friends
                try
                {
                    CREDENTIAL cred = (CREDENTIAL)Marshal.PtrToStructure(ptr, typeof(CREDENTIAL));
                    int size = cred.CredentialBlobSize;
                    if (size <= 0 || cred.CredentialBlob == IntPtr.Zero) return null;

                    byte[] bytes = new byte[size];
                    Marshal.Copy(cred.CredentialBlob, bytes, 0, size);
                    return Encoding.UTF8.GetString(bytes);
                }
                finally
                {
                    CredFree(ptr);
                }
            }
            catch (Exception)
            {
                // wincred.js resolves null for every failure mode (spawn error, timeout, bad
                // JSON); the adapters treat that as "no credential here" and keep going.
                return null;
            }
        }
    }
}
