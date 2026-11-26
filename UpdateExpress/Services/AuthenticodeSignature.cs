using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace UpdateExpress.Services;

/// <summary>
/// The embedded Authenticode signature of a file, as Windows (WinVerifyTrust) judges it, with
/// the signer and root certificates of the chain Windows built for it.
/// </summary>
internal sealed record AuthenticodeSignature(bool Trusted, int TrustError, string SignerName, string SignerOrganization, string RootName, string RootOrganization)
{
    private const string Microsoft = "Microsoft Corporation";
    private const string MicrosoftRootPrefix = "Microsoft Root Certificate Authority";

    /// <summary>
    /// Trusted by Windows, signed by Microsoft Corporation, and chained to one of Microsoft's
    /// own root certificates.
    /// </summary>
    public bool IsMicrosoft =>
        Trusted &&
        string.Equals(SignerOrganization, Microsoft, StringComparison.Ordinal) &&
        string.Equals(RootOrganization, Microsoft, StringComparison.Ordinal) &&
        RootName.StartsWith(MicrosoftRootPrefix, StringComparison.Ordinal);

    public string Describe() => Trusted
        ? $"Signer: {Show(SignerName)} ({Show(SignerOrganization)}); root: {Show(RootName)}."
        : $"Windows does not trust the signature (0x{TrustError:X8}).";

    private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;

    internal static AuthenticodeSignature Read(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path
        };
        IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = WTD_CHOICE_FILE,
            pFile = filePtr,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
        };
        bool structMarshalled = false;
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            structMarshalled = true;
            int result = WinVerifyTrust(InvalidHandle, ref GenericVerifyV2, ref data);
            if (result != 0) return new(false, result, "", "", "", "");

            var chain = ReadSignerChain(data.hWVTStateData);
            try
            {
                if (chain.Count == 0) return new(false, 0, "", "", "", "");
                var signer = chain[0];
                var root = chain[^1];
                // A root is its own issuer; a chain that ends in anything else has no root to name.
                bool endsInRoot = chain.Count > 1 &&
                    root.SubjectName.RawData.AsSpan().SequenceEqual(root.IssuerName.RawData);
                return new(true, 0,
                    NamePart(signer.SubjectName, CommonNameOid),
                    NamePart(signer.SubjectName, OrganizationOid),
                    endsInRoot ? NamePart(root.SubjectName, CommonNameOid) : "",
                    endsInRoot ? NamePart(root.SubjectName, OrganizationOid) : "");
            }
            finally
            {
                foreach (var certificate in chain) certificate.Dispose();
            }
        }
        finally
        {
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            try { WinVerifyTrust(InvalidHandle, ref GenericVerifyV2, ref data); } catch { }
            if (structMarshalled) Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

    private static List<X509Certificate2> ReadSignerChain(IntPtr stateData)
    {
        var chain = new List<X509Certificate2>();
        if (stateData == IntPtr.Zero) return chain;
        IntPtr provider = WTHelperProvDataFromStateData(stateData);
        if (provider == IntPtr.Zero) return chain;
        IntPtr signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == IntPtr.Zero) return chain;
        // CRYPT_PROVIDER_SGNR: cbStruct (4), sftVerifyAsOf (8), csCertChain (4), ...
        int count = Marshal.ReadInt32(signer, 12);
        for (int i = 0; i < count && i < 16; i++)
        {
            IntPtr providerCert = WTHelperGetProvCertFromChain(signer, (uint)i);
            if (providerCert == IntPtr.Zero) break;
            // CRYPT_PROVIDER_CERT: cbStruct (4, then padding to pointer size), pCert.
            IntPtr certContext = Marshal.ReadIntPtr(providerCert, IntPtr.Size);
            if (certContext == IntPtr.Zero) break;
            var context = Marshal.PtrToStructure<CERT_CONTEXT>(certContext);
            if (context.pbCertEncoded == IntPtr.Zero || context.cbCertEncoded == 0) break;
            var encoded = new byte[context.cbCertEncoded];
            Marshal.Copy(context.pbCertEncoded, encoded, 0, encoded.Length);
            chain.Add(X509CertificateLoader.LoadCertificate(encoded));
        }
        return chain;
    }

    private const string CommonNameOid = "2.5.4.3";
    private const string OrganizationOid = "2.5.4.10";

    private static string NamePart(X500DistinguishedName name, string oid)
    {
        foreach (var part in name.EnumerateRelativeDistinguishedNames())
        {
            if (part.HasMultipleElements) continue;
            if (part.GetSingleElementType().Value == oid)
                return part.GetSingleElementValue() ?? "";
        }
        return "";
    }

    private static readonly IntPtr InvalidHandle = new(-1);
    private static Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CERT_CONTEXT
    {
        public uint dwCertEncodingType;
        public IntPtr pbCertEncoded;
        public uint cbCertEncoded;
        public IntPtr pCertInfo;
        public IntPtr hCertStore;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr pSgnr, uint idxCert);
}
