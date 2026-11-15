// FrameDoubler live engine: capture -> duplicate removal -> anti-aliasing -> frame
// generation -> preview / recording. C API used by the WPF app (fdlive.dll) and by
// fd-live.exe for command-line checks.
#pragma once
#ifdef FDL_DLL
#define FDL_API extern "C" __declspec(dllexport)
#else
#define FDL_API extern "C"
#endif
#include <windows.h>

// Strings returned by the API are UTF-8, allocated with CoTaskMemAlloc; free them with
// fdl_free. Settings and commands are "key=value" lines.
FDL_API int fdl_api_version();
FDL_API char* fdl_devices();
FDL_API void* fdl_open(const char* settings, HWND window, char** error);
FDL_API int fdl_command(void* session, const char* command);
FDL_API char* fdl_status(void* session);
FDL_API void fdl_close(void* session);
FDL_API char* fdl_selftest(const char* settings);
// DLSS-G capability of the high-performance adapter: {"maxMultiplier":N,"error":"..."} (settings: dlssDir, dlssDataDir).
FDL_API char* fdl_dlss_probe(const char* settings);
FDL_API void fdl_free(void* text);

// Headless replay (fd-live.exe and tests): frames are fed by the caller.
FDL_API void* fdl_open_replay(const char* settings, char** error);
FDL_API int fdl_feed(void* session, const unsigned char* data, int pitch, long long time100ns);
FDL_API int fdl_feed_audio(void* session, const float* samples, int frames, int channels, int rate, long long time100ns);
FDL_API char* fdl_summary(void* session);
