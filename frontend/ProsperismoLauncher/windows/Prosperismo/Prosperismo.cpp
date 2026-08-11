// Prosperismo.cpp : Defines the entry point for the application.
//

#include "pch.h"
#include "Prosperismo.h"

#include "AutolinkedNativeModules.g.h"

#include "NativeModules.h"
#include "ProsperismoFocusRing.h"
#include "ProsperismoLocalImage.h"
#include "ProsperismoLocalImageProvider.h"
#include "ProsperismoNativeBackground.h"

#include <strsafe.h>

// A PackageProvider containing native modules defined within this app project.
struct CompReactPackageProvider
    : winrt::implements<CompReactPackageProvider, winrt::Microsoft::ReactNative::IReactPackageProvider> {
 public:
  void CreatePackage(winrt::Microsoft::ReactNative::IReactPackageBuilder const &packageBuilder) noexcept {
    AddAttributedModules(packageBuilder, true);
    RegisterProsperismoLocalImageProvider(packageBuilder);
    RegisterProsperismoNativeBackground(packageBuilder);
    RegisterProsperismoFocusRing(packageBuilder);
    RegisterProsperismoLocalImage(packageBuilder);
  }
};

namespace {

wchar_t const *g_startupPhase = L"process entry";

void AppendStartupLog(wchar_t const *event, wchar_t const *detail = L"") noexcept {
  wchar_t localAppData[32768]{};
  DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, ARRAYSIZE(localAppData));
  if (length == 0 || length >= ARRAYSIZE(localAppData)) {
    return;
  }
  wchar_t directory[32768]{};
  if (FAILED(StringCchPrintfW(directory, ARRAYSIZE(directory), L"%s\\Prosperismo", localAppData))) {
    return;
  }
  CreateDirectoryW(directory, nullptr);
  wchar_t path[32768]{};
  if (FAILED(StringCchPrintfW(path, ARRAYSIZE(path), L"%s\\launcher-startup.log", directory))) {
    return;
  }

  HANDLE file = CreateFileW(
      path,
      FILE_APPEND_DATA,
      FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
      nullptr,
      OPEN_ALWAYS,
      FILE_ATTRIBUTE_NORMAL,
      nullptr);
  if (file == INVALID_HANDLE_VALUE) {
    return;
  }
  SYSTEMTIME now{};
  GetSystemTime(&now);
  wchar_t line[2048]{};
  if (SUCCEEDED(StringCchPrintfW(
          line,
          ARRAYSIZE(line),
          L"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ pid=%lu phase=%s%s%s\r\n",
          now.wYear,
          now.wMonth,
          now.wDay,
          now.wHour,
          now.wMinute,
          now.wSecond,
          now.wMilliseconds,
          GetCurrentProcessId(),
          event,
          detail[0] ? L" detail=" : L"",
          detail))) {
    int bytesRequired = WideCharToMultiByte(CP_UTF8, 0, line, -1, nullptr, 0, nullptr, nullptr);
    if (bytesRequired > 1) {
      std::string utf8(static_cast<size_t>(bytesRequired), '\0');
      WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8.data(), bytesRequired, nullptr, nullptr);
      DWORD written = 0;
      WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size() - 1), &written, nullptr);
      FlushFileBuffers(file);
    }
  }
  CloseHandle(file);
}

void EnterStartupPhase(wchar_t const *phase) noexcept {
  g_startupPhase = phase;
  AppendStartupLog(phase);
}

LONG WINAPI LogUnhandledException(EXCEPTION_POINTERS *exception) noexcept {
  wchar_t detail[256]{};
  StringCchPrintfW(
      detail,
      ARRAYSIZE(detail),
      L"last=%s code=0x%08lx address=%p",
      g_startupPhase,
      exception && exception->ExceptionRecord ? exception->ExceptionRecord->ExceptionCode : 0,
      exception && exception->ExceptionRecord ? exception->ExceptionRecord->ExceptionAddress : nullptr);
  AppendStartupLog(L"unhandled structured exception", detail);
  return EXCEPTION_CONTINUE_SEARCH;
}

bool HostModuleDisabled() noexcept {
  wchar_t value[8]{};
  DWORD length = GetEnvironmentVariableW(L"PROSPERISMO_DISABLE_HOST", value, ARRAYSIZE(value));
  return length > 0 && length < ARRAYSIZE(value) && value[0] != L'0';
}

int RunProsperismo() {
  try {
    EnterStartupPhase(L"initialize WinRT apartment");
    winrt::init_apartment(winrt::apartment_type::single_threaded);

    EnterStartupPhase(L"configure process DPI");
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    EnterStartupPhase(L"resolve application directory");
    WCHAR appDirectory[MAX_PATH];
    DWORD appDirectoryLength = GetModuleFileNameW(nullptr, appDirectory, ARRAYSIZE(appDirectory));
    if (appDirectoryLength == 0 || appDirectoryLength >= ARRAYSIZE(appDirectory) ||
        FAILED(PathCchRemoveFileSpec(appDirectory, ARRAYSIZE(appDirectory)))) {
      throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()), L"Could not resolve the launcher directory.");
    }

    EnterStartupPhase(L"build React Native application");
    auto reactNativeWin32App{winrt::Microsoft::ReactNative::ReactNativeAppBuilder().Build()};

    EnterStartupPhase(L"configure React Native host");
    auto settings{reactNativeWin32App.ReactNativeHost().InstanceSettings()};
    settings.NativeLogger(winrt::Microsoft::ReactNative::LogHandler{
        [](winrt::Microsoft::ReactNative::LogLevel level, winrt::hstring const &message) noexcept {
          wchar_t detail[1536]{};
          StringCchPrintfW(
              detail,
              ARRAYSIZE(detail),
              L"level=%d message=%s",
              static_cast<int>(level),
              message.c_str());
          AppendStartupLog(L"react log", detail);
        }});
    RegisterAutolinkedNativeModulePackages(settings.PackageProviders());
    if (!HostModuleDisabled()) {
      settings.PackageProviders().Append(winrt::make<CompReactPackageProvider>());
      AppendStartupLog(L"registered ProsperismoHost");
    } else {
      AppendStartupLog(L"ProsperismoHost disabled by diagnostic override");
    }

#if BUNDLE
    settings.BundleRootPath(std::wstring(L"file://").append(appDirectory).append(L"\\Bundle\\").c_str());
    settings.JavaScriptBundleFile(L"index.windows");
    settings.UseFastRefresh(false);
#else
    settings.JavaScriptBundleFile(L"index");
    settings.UseFastRefresh(true);
#endif
#if _DEBUG
    settings.UseDirectDebugger(true);
    settings.UseDeveloperSupport(true);
#else
    settings.UseDirectDebugger(false);
    settings.UseDeveloperSupport(false);
#endif

    EnterStartupPhase(L"get application window");
    auto appWindow{reactNativeWin32App.AppWindow()};
    EnterStartupPhase(L"set application window title");
    appWindow.Title(L"Prosperismo");
    EnterStartupPhase(L"resize application window");
    appWindow.ResizeClient({1600, 900});
    EnterStartupPhase(L"get React view options");
    auto viewOptions{reactNativeWin32App.ReactViewOptions()};
    EnterStartupPhase(L"set React component name");
    viewOptions.ComponentName(L"Prosperismo");

    EnterStartupPhase(L"start React Native application");
    reactNativeWin32App.Start();
    AppendStartupLog(L"React Native application stopped normally");
    return 0;
  } catch (winrt::hresult_error const &error) {
    wchar_t detail[1024]{};
    StringCchPrintfW(
        detail,
        ARRAYSIZE(detail),
        L"last=%s hresult=0x%08lx message=%s",
        g_startupPhase,
        static_cast<unsigned long>(error.code().value),
        error.message().c_str());
    AppendStartupLog(L"WinRT startup failure", detail);
    MessageBoxW(nullptr, detail, L"Prosperismo could not start", MB_OK | MB_ICONERROR);
    return static_cast<int>(error.code().value);
  } catch (std::exception const &error) {
    wchar_t detail[1024]{};
    MultiByteToWideChar(CP_UTF8, 0, error.what(), -1, detail, ARRAYSIZE(detail));
    AppendStartupLog(L"C++ startup failure", detail);
    MessageBoxW(nullptr, detail, L"Prosperismo could not start", MB_OK | MB_ICONERROR);
    return EXIT_FAILURE;
  } catch (...) {
    AppendStartupLog(L"unknown startup failure", g_startupPhase);
    MessageBoxW(nullptr, g_startupPhase, L"Prosperismo could not start", MB_OK | MB_ICONERROR);
    return EXIT_FAILURE;
  }
}

} // namespace

// The entry point of the Win32 application
_Use_decl_annotations_ int CALLBACK WinMain(
    HINSTANCE /*instance*/, HINSTANCE, PSTR /*commandLine*/, int /*showCmd*/) {
  SetUnhandledExceptionFilter(LogUnhandledException);
  AppendStartupLog(L"WinMain entered");
  return RunProsperismo();
}
