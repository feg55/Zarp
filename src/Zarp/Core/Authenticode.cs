using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Zarp.Core
{
    /// <summary>
    /// Проверка подписи Authenticode средствами Windows (WinVerifyTrust), тем же механизмом, что у `signtool verify`.
    /// Не зависит от PowerShell: у него бывают сломанные модули, ограничения политик и чужой PSModulePath
    /// (на серверах GitHub Windows PowerShell, запущенный из PowerShell 7, не находил Get-AuthenticodeSignature).
    /// Zarp проверяет подпись чужого установщика, который потом запускается с правами администратора,
    /// поэтому молчание проверяющего не должно приниматься за ответ.
    /// </summary>
    public static class Authenticode
    {
        public sealed class Result
        {
            /// <summary>Подпись есть, файл не менялся после подписи, цепочка ведёт к доверенному корню Windows.</summary>
            public bool Trusted;
            /// <summary>Короткое название причины отказа: NotSigned, HashMismatch, NotTrusted и так далее.</summary>
            public string Problem;
            /// <summary>Субъект сертификата подписавшего (CN="Cloudflare, Inc.", O=...), если подпись принята.</summary>
            public string SignerSubject;
            /// <summary>Original DER Name from the verified signer certificate.</summary>
            public byte[] SignerSubjectRaw;
        }

        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        static readonly IntPtr NoWindow = new IntPtr(-1); // INVALID_HANDLE_VALUE: окон не показывать

        const uint WTD_UI_NONE = 2;
        const uint WTD_REVOKE_NONE = 0;
        const uint WTD_CHOICE_FILE = 1;
        const uint WTD_STATEACTION_VERIFY = 1;
        const uint WTD_STATEACTION_CLOSE = 2;
        // Сертификаты не проверяются на отзыв: это сеть (CRL и OCSP), а Zarp нужен именно там, где сеть ограничена.
        const uint WTD_REVOCATION_CHECK_NONE = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
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

        [DllImport("wintrust.dll", ExactSpelling = true)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signerIndex,
            [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIndex);

        /// <summary>Проверить встроенную подпись файла (exe, dll, msi). Исключений не бросает: результат всегда в Result.</summary>
        public static Result Verify(string file)
        {
            var result = new Result();
            IntPtr path = IntPtr.Zero, info = IntPtr.Zero;
            var data = new WINTRUST_DATA();
            var action = GenericVerifyV2;
            bool opened = false;
            try
            {
                path = Marshal.StringToHGlobalUni(file);
                info = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
                Marshal.StructureToPtr(new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                    pcwszFilePath = path,
                }, info, false);

                data.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA));
                data.dwUIChoice = WTD_UI_NONE;
                data.fdwRevocationChecks = WTD_REVOKE_NONE;
                data.dwUnionChoice = WTD_CHOICE_FILE;
                data.pFile = info;
                data.dwStateAction = WTD_STATEACTION_VERIFY;
                data.dwProvFlags = WTD_REVOCATION_CHECK_NONE;

                opened = true;
                uint code = unchecked((uint)WinVerifyTrust(NoWindow, ref action, ref data));
                if (code == 0)
                {
                    result.Trusted = true;
                    ReadSigner(data.hWVTStateData, result);
                }
                else
                {
                    result.Problem = Describe(code);
                }
            }
            catch (Exception e)
            {
                result.Trusted = false;
                result.Problem = "error: " + e.Message;
            }
            finally
            {
                if (opened)
                {
                    // после каждой проверки, удачной или нет, состояние нужно закрыть: иначе оно утечёт
                    data.dwStateAction = WTD_STATEACTION_CLOSE;
                    try { WinVerifyTrust(NoWindow, ref action, ref data); } catch { }
                }
                if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
                if (path != IntPtr.Zero) Marshal.FreeHGlobal(path);
            }
            return result;
        }

        /// <summary>Сертификат подписавшего (первая подпись файла) из состояния проверки.</summary>
        static void ReadSigner(IntPtr state, Result result)
        {
            if (state == IntPtr.Zero) return;
            IntPtr provider = WTHelperProvDataFromStateData(state);
            if (provider == IntPtr.Zero) return;
            IntPtr signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == IntPtr.Zero) return;
            IntPtr providerCert = WTHelperGetProvCertFromChain(signer, 0);
            if (providerCert == IntPtr.Zero) return;
            // CRYPT_PROVIDER_CERT: DWORD cbStruct, затем указатель на CERT_CONTEXT
            IntPtr certContext = Marshal.ReadIntPtr(providerCert, IntPtr.Size);
            if (certContext == IntPtr.Zero) return;
            using (var cert = new X509Certificate2(certContext))
            {
                result.SignerSubject = cert.Subject;
                result.SignerSubjectRaw = cert.SubjectName.RawData;
            }
        }

        /// <summary>Код WinVerifyTrust в короткое английское название: оно попадает в сообщение об ошибке и журнал.</summary>
        internal static string Describe(uint code)
        {
            switch (code)
            {
                case 0x800B0100: return "NotSigned";       // TRUST_E_NOSIGNATURE
                case 0x800B0003: return "UnknownFormat";   // TRUST_E_SUBJECT_FORM_UNKNOWN
                case 0x80096010: return "HashMismatch";    // TRUST_E_BAD_DIGEST: файл изменён после подписи
                case 0x80096004: return "BadSignature";    // TRUST_E_CERT_SIGNATURE
                case 0x800B0101: return "Expired";         // CERT_E_EXPIRED
                case 0x800B010C: return "Revoked";         // CERT_E_REVOKED
                case 0x800B0004:                           // TRUST_E_SUBJECT_NOT_TRUSTED
                case 0x800B0109:                           // CERT_E_UNTRUSTEDROOT
                case 0x800B010A:                           // CERT_E_CHAINING
                case 0x800B010D:                           // CERT_E_UNTRUSTEDTESTROOT
                case 0x800B0111: return "NotTrusted";      // TRUST_E_EXPLICIT_DISTRUST
                case 0x80070002:
                case 0x80070003: return "FileNotFound";
                case 0x80092003: return "FileError";       // CRYPT_E_FILE_ERROR
                default: return "error 0x" + code.ToString("X8");
            }
        }
    }
}
