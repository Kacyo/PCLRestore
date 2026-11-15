/*
 * bootstrap32.dll
 * 32 位原生引导 DLL：被注入到 PCL2 进程后，通过 mscoree!CorBindToRuntimeEx
 * 拿到 ICLRRuntimeHost，再调用 ExecuteInDefaultAppDomain 在默认 AppDomain 里
 * 执行托管 payload（PCLRestore.Payload.Init）。
 *
 * 编译：gcc -m32 -shared -O2 -o bootstrap32.dll bootstrap.c -lole32
 */
#include <windows.h>

static const GUID CLSID_CLRRuntimeHost =
    { 0x90F1A06E, 0x7712, 0x4762, { 0x86, 0xB5, 0x7A, 0x5E, 0xBA, 0x6B, 0xDB, 0x02 } };
static const GUID IID_ICLRRuntimeHost =
    { 0x90F1A06C, 0x7712, 0x4762, { 0x86, 0xB5, 0x7A, 0x5E, 0xBA, 0x6B, 0xDB, 0x02 } };

/* ICLRRuntimeHost 虚表下标：IUnknown(3) + Start/Stop/SetHostControl/GetCLRControl/
   UnloadAppDomain/ExecuteInAppDomain/GetCurrentAppDomainId/ExecuteApplication = 8 */
#define VT_EXEC_IN_DEFAULT_APPDOMAIN 11

typedef HRESULT (__stdcall *PFN_CorBindToRuntimeEx)(LPCWSTR, LPCWSTR, DWORD,
                                                    const GUID *, const GUID *, void **);
typedef HRESULT (__stdcall *PFN_ExecInDefaultAppDomain)(void *, LPCWSTR, LPCWSTR,
                                                        LPCWSTR, LPCWSTR, DWORD *);

static HINSTANCE g_hInst;
static WCHAR g_logPath[MAX_PATH];

static void LogA(const char *msg)
{
    HANDLE h;
    DWORD written;
    int len = 0;
    while (msg[len]) len++;

    h = CreateFileW(g_logPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    WriteFile(h, msg, (DWORD)len, &written, NULL);
    WriteFile(h, "\r\n", 2, &written, NULL);
    CloseHandle(h);
}

static void LogHexA(const char *prefix, DWORD value)
{
    const char *digits = "0123456789ABCDEF";
    char buf[64];
    int i = 0, shifted = 28, started = 0;

    while (prefix[i]) { buf[i] = prefix[i]; i++; }
    buf[i++] = '0'; buf[i++] = 'x';
    for (; shifted >= 0; shifted -= 4) {
        int nibble = (int)((value >> shifted) & 0xF);
        if (nibble != 0 || started || shifted == 0) { buf[i++] = digits[nibble]; started = 1; }
    }
    buf[i] = 0;
    LogA(buf);
}

/* 拼出 payload 路径：与 bootstrap32.dll 同目录下的 PCLRestore.Payload.dll */
static void BuildPayloadPath(LPWSTR out, DWORD cch)
{
    WCHAR self[MAX_PATH];
    DWORD n = GetModuleFileNameW(g_hInst, self, MAX_PATH);
    DWORD i;

    if (n == 0 || n >= MAX_PATH) { lstrcpynW(out, L"PCLRestore.Payload.dll", cch); return; }

    for (i = n; i > 0; i--) { if (self[i - 1] == L'\\') break; }
    if (i == 0) { lstrcpynW(out, L"PCLRestore.Payload.dll", cch); return; }

    lstrcpynW(out, self, (int)i + 1);
    lstrcatW(out, L"PCLRestore.Payload.dll");
}

static DWORD WINAPI Worker(LPVOID unused)
{
    HMODULE mscoree;
    PFN_CorBindToRuntimeEx bind;
    PFN_ExecInDefaultAppDomain exec;
    void **vtbl;
    void *host = NULL;
    WCHAR payload[MAX_PATH];
    DWORD retVal = 0;
    HRESULT hr;

    (void)unused;
    LogA("[bootstrap] worker start");

    BuildPayloadPath(payload, MAX_PATH);
    LogA("[bootstrap] payload path = ");
    {
        char narrow[MAX_PATH * 2];
        int n = WideCharToMultiByte(CP_ACP, 0, payload, -1, narrow, sizeof(narrow), NULL, NULL);
        if (n > 0) LogA(narrow);
    }

    mscoree = LoadLibraryW(L"mscoree.dll");
    if (!mscoree) { LogHexA("[bootstrap] LoadLibrary(mscoree) failed, err=", GetLastError()); return 1; }

    bind = (PFN_CorBindToRuntimeEx)GetProcAddress(mscoree, "CorBindToRuntimeEx");
    if (!bind) { LogA("[bootstrap] CorBindToRuntimeEx not found"); return 2; }

    hr = bind(L"v4.0.30319", NULL, 0, &CLSID_CLRRuntimeHost, &IID_ICLRRuntimeHost, &host);
    LogHexA("[bootstrap] CorBindToRuntimeEx hr=", (DWORD)hr);
    if (FAILED(hr) || !host) { LogA("[bootstrap] no runtime host"); return 3; }

    vtbl = *(void ***)host;
    exec = (PFN_ExecInDefaultAppDomain)vtbl[VT_EXEC_IN_DEFAULT_APPDOMAIN];

    hr = exec(host, payload, L"PCLRestore.Payload", L"Init", L"", &retVal);
    LogHexA("[bootstrap] ExecuteInDefaultAppDomain hr=", (DWORD)hr);
    LogHexA("[bootstrap] payload return=", retVal);

    ((ULONG (__stdcall *)(void *))vtbl[2])(host);
    LogA("[bootstrap] done");
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved)
{
    HANDLE th;
    WCHAR self[MAX_PATH];
    DWORD i, n;

    (void)reserved;
    if (reason != DLL_PROCESS_ATTACH) return TRUE;

    g_hInst = hinst;
    DisableThreadLibraryCalls(hinst);

    /* 日志与 DLL 同目录 */
    n = GetModuleFileNameW(hinst, self, MAX_PATH);
    if (n > 0 && n < MAX_PATH) {
        for (i = n; i > 0; i--) { if (self[i - 1] == L'\\') break; }
        lstrcpynW(g_logPath, self, (int)i + 1);
        lstrcatW(g_logPath, L"bootstrap.log");
    } else {
        lstrcpynW(g_logPath, L"bootstrap.log", MAX_PATH);
    }

    th = CreateThread(NULL, 0, Worker, NULL, 0, NULL);
    if (th) CloseHandle(th);
    return TRUE;
}
