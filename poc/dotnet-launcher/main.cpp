// MIT. Small app-local .NET host: the full runtime and managed assemblies live in dll/.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <filesystem>
#include <string>
#include <cstdio>

namespace fs = std::filesystem;
static std::wstring errors;
static void __cdecl record_error(const wchar_t* message) { errors += message; errors += L"\n"; }
static int fail(const std::wstring& message, int code) {
#ifdef FD_GUI
    MessageBoxW(nullptr, message.c_str(), L"FrameDoubler", MB_OK | MB_ICONERROR);
#else
    fwprintf(stderr, L"%ls\n", message.c_str());
#endif
    return code;
}
static int run(int argc, wchar_t** argv) {
    wchar_t path[32768];
    DWORD length = GetModuleFileNameW(nullptr, path, 32768);
    if (!length || length >= 32768) return fail(L"Cannot locate FrameDoubler.", 1);
    fs::path host(path), root = host.parent_path(), support = root / L"dll";
#ifdef FD_GUI
    fs::path managed = support / L"FrameDoubler.dll";
#else
    fs::path managed = support / L"FrameDoubler.Cli.dll";
#endif
    fs::path runtime = support / L"hostfxr.dll";
    if (!fs::is_regular_file(managed) || !fs::is_regular_file(runtime))
        return fail(L"The dll folder is missing or incomplete. Extract the entire FrameDoubler ZIP before running.\n\n프로그램의 dll 폴더가 없거나 불완전합니다. ZIP 전체를 다시 풀어 주세요.", 1);
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    auto cookie = AddDllDirectory(support.c_str());
    HMODULE module = LoadLibraryExW(runtime.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!module) return fail(L"Cannot load the bundled .NET runtime from dll/. Re-extract the ZIP.", 1);
    using start_fn = int(__cdecl*)(int, const wchar_t**, const wchar_t*, const wchar_t*, const wchar_t*);
    using writer_fn = void(__cdecl*)(const wchar_t*);
    using set_writer_fn = writer_fn(__cdecl*)(writer_fn);
    auto start = reinterpret_cast<start_fn>(GetProcAddress(module, "hostfxr_main_startupinfo"));
    auto writer = reinterpret_cast<set_writer_fn>(GetProcAddress(module, "hostfxr_set_error_writer"));
    if (!start) return fail(L"The bundled .NET runtime is incompatible.", 1);
    if (writer) writer(record_error);
    int result = start(argc, const_cast<const wchar_t**>(argv), host.c_str(), support.c_str(), managed.c_str());
    if (result < 0 && !errors.empty()) fail(errors, result);
    if (cookie) RemoveDllDirectory(cookie);
    FreeLibrary(module);
    return result;
}
#ifdef FD_GUI
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int argc = 0; auto argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return 1;
    int result = run(argc, argv); LocalFree(argv); return result;
}
#else
int wmain(int argc, wchar_t** argv) { return run(argc, argv); }
#endif
