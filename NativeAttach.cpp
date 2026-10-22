#define NOMINMAX
#include <windows.h>
#include <tlhelp32.h>
#include <wincrypt.h>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <vector>
#include <string>

namespace {
constexpr char expected_hash[] =
    "F0DB93660CEB8635A1921E0BA4686FCFBD5C492311DEE6EF7061B7BADA16731E";

std::wstring module_path(DWORD pid, const wchar_t *name, uintptr_t *base = nullptr) {
    HANDLE snapshot = INVALID_HANDLE_VALUE;
    for (int i = 0; i < 4; ++i) {
        snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
        if (snapshot != INVALID_HANDLE_VALUE || GetLastError() != ERROR_BAD_LENGTH) break;
    }
    if (snapshot == INVALID_HANDLE_VALUE) {
        std::fprintf(stderr, "module snapshot failed pid=%lu error=%lu\n", pid, GetLastError());
        return {};
    }
    MODULEENTRY32W item = {}; item.dwSize = sizeof(item);
    std::wstring result;
    if (Module32FirstW(snapshot, &item)) {
        do {
            if (_wcsicmp(item.szModule, name) == 0) {
                result = item.szExePath;
                if (base) *base = reinterpret_cast<uintptr_t>(item.modBaseAddr);
                break;
            }
        } while (Module32NextW(snapshot, &item));
    }
    CloseHandle(snapshot);
    return result;
}

bool file_is_verified_game(const std::wstring &path) {
    if (path.empty()) return false;
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        std::fwprintf(stderr, L"game file open failed path=%ls error=%lu\n", path.c_str(), GetLastError());
        return false;
    }
    HCRYPTPROV provider = 0;
    HCRYPTHASH hash = 0;
    bool okay = CryptAcquireContextW(&provider, nullptr, nullptr, PROV_RSA_AES, CRYPT_VERIFYCONTEXT) &&
        CryptCreateHash(provider, CALG_SHA_256, 0, 0, &hash);
    unsigned char buffer[65536];
    DWORD count = 0;
    while (okay) {
        if (!ReadFile(file, buffer, sizeof(buffer), &count, nullptr)) { okay = false; break; }
        if (!count) break;
        okay = CryptHashData(hash, buffer, count, 0) != FALSE;
    }
    BYTE digest[32] = {}; DWORD length = sizeof(digest);
    okay = okay && CryptGetHashParam(hash, HP_HASHVAL, digest, &length, 0) && length == 32;
    char hex[65] = {};
    if (okay) {
        for (unsigned i = 0; i < 32; ++i)
            sprintf_s(hex + i * 2, sizeof(hex) - i * 2, "%02X", digest[i]);
        okay = std::strcmp(hex, expected_hash) == 0;
    }
    if (!okay) std::fprintf(stderr, "game file hash failed calculated=%s error=%lu\n", hex, GetLastError());
    if (hash) CryptDestroyHash(hash);
    if (provider) CryptReleaseContext(provider, 0);
    CloseHandle(file);
    return okay;
}

bool call_remote(HANDLE process, uintptr_t address, void *argument, DWORD &result) {
    HANDLE thread = CreateRemoteThread(process, nullptr, 0,
        reinterpret_cast<LPTHREAD_START_ROUTINE>(address), argument, 0, nullptr);
    if (!thread) return false;
    const DWORD waited = WaitForSingleObject(thread, 10000);
    const bool okay = waited == WAIT_OBJECT_0 && GetExitCodeThread(thread, &result);
    CloseHandle(thread);
    return okay;
}

std::wstring basename(const std::wstring &path) {
    const auto slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? path : path.substr(slash + 1);
}
}

int wmain(int argc, wchar_t **argv) {
    if (argc != 4) { std::fwprintf(stderr, L"usage: NativeAttach.exe PID NativeProbe.dll GAME_HWND\n"); return 2; }
    const DWORD pid = wcstoul(argv[1], nullptr, 10);
    if (!pid) return 2;
    const uintptr_t game_window = static_cast<uintptr_t>(_wcstoui64(argv[3], nullptr, 10));
    DWORD window_owner = 0;
    bool window_valid = game_window && IsWindow(reinterpret_cast<HWND>(game_window));
    if (!window_valid && game_window) {
        HDESK input_desktop = OpenInputDesktop(0, FALSE,
            DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS);
        if (input_desktop) {
            SetThreadDesktop(input_desktop);
            window_valid = IsWindow(reinterpret_cast<HWND>(game_window)) != FALSE;
            CloseDesktop(input_desktop);
        }
    }
    const DWORD window_thread = window_valid
        ? GetWindowThreadProcessId(reinterpret_cast<HWND>(game_window), &window_owner) : 0;
    if (!window_thread || window_owner != pid) {
        std::fprintf(stderr, "game window does not belong to verified PID: hwnd=%p valid=%d thread=%lu owner=%lu expected=%lu error=%lu\n",
            reinterpret_cast<void *>(game_window), window_valid, window_thread,
            window_owner, pid, GetLastError());
        return 2;
    }
    const std::wstring dll = argv[2];
    if (!file_is_verified_game(module_path(pid, L"th123.exe"))) {
        std::fprintf(stderr, "target is not verified th123.exe 1.10a\n"); return 3;
    }
    HANDLE process = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
        PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid);
    if (!process) { std::fprintf(stderr, "OpenProcess failed=%lu\n", GetLastError()); return 4; }

    uintptr_t remote_base = 0;
    const auto existing = module_path(pid, basename(dll).c_str(), &remote_base);
    if (!existing.empty() && _wcsicmp(existing.c_str(), dll.c_str()) != 0) {
        std::fprintf(stderr, "different probe with same module name is loaded\n");
        CloseHandle(process); return 5;
    }
    if (!remote_base) {
        const auto local_load = reinterpret_cast<uintptr_t>(
            GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
        HMODULE local_owner = nullptr;
        if (!local_load || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(local_load), &local_owner)) {
            CloseHandle(process); return 6;
        }
        wchar_t owner_path[MAX_PATH] = {};
        GetModuleFileNameW(local_owner, owner_path, MAX_PATH);
        uintptr_t remote_owner = 0;
        module_path(pid, basename(owner_path).c_str(), &remote_owner);
        const auto local_base = reinterpret_cast<uintptr_t>(local_owner);
        if (!remote_owner) { CloseHandle(process); return 6; }
        const auto bytes = (dll.size() + 1) * sizeof(wchar_t);
        void *remote_path = VirtualAllocEx(process, nullptr, bytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (!remote_path || !WriteProcessMemory(process, remote_path, dll.c_str(), bytes, nullptr)) {
            std::fprintf(stderr, "remote path write failed=%lu\n", GetLastError());
            if (remote_path) VirtualFreeEx(process, remote_path, 0, MEM_RELEASE);
            CloseHandle(process); return 7;
        }
        DWORD loaded = 0;
        const bool okay = call_remote(process, remote_owner + local_load - local_base,
            remote_path, loaded);
        if (okay) VirtualFreeEx(process, remote_path, 0, MEM_RELEASE);
        if (!okay || !loaded) {
            std::fprintf(stderr, "remote LoadLibraryW failed=%lu\n", GetLastError());
            CloseHandle(process); return 8;
        }
        remote_base = loaded;
    }
    HMODULE local = LoadLibraryExW(dll.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
    if (!local) { CloseHandle(process); return 9; }
    FARPROC initialize = GetProcAddress(local, "_InitializeReplayProbe@4");
    if (!initialize) initialize = GetProcAddress(local, "InitializeReplayProbe");
    if (!initialize) {
        std::fprintf(stderr, "probe initialize export missing\n");
        FreeLibrary(local); CloseHandle(process); return 10;
    }
    DWORD initialized = 0;
    const bool okay = call_remote(process,
        remote_base + reinterpret_cast<uintptr_t>(initialize) - reinterpret_cast<uintptr_t>(local),
        reinterpret_cast<void *>(game_window), initialized);
    FreeLibrary(local);
    CloseHandle(process);
    if (!okay || initialized != 1) {
        std::fprintf(stderr, "probe initialization failed result=%lu error=%lu\n",
            initialized, GetLastError());
        return 11;
    }
    std::printf("probe attached pid=%lu\n", pid);
    return 0;
}
