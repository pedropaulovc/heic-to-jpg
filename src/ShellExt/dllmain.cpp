// IExplorerCommand shell extension: adds "Convert to JPEG" to the Windows 11
// top-level context menu for .heic/.heif files.
//
// This DLL deliberately does no image work. It runs inside Explorer's COM
// surrogate, where a hang stalls the shell and a crash is blamed on Explorer.
// Its entire job is to read the selection and hand the paths to heic2jpg.exe.

#include <windows.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <string>
#include <new>

#pragma comment(lib, "shlwapi.lib")

#include "resource.h"

// {7BF1D449-FD9B-4E96-8C33-88F9A85D4EC1}
static const CLSID CLSID_ConvertToJpeg =
    { 0x7bf1d449, 0xfd9b, 0x4e96, { 0x8c, 0x33, 0x88, 0xf9, 0xa8, 0x5d, 0x4e, 0xc1 } };

static const wchar_t kConverterExe[] = L"heic2jpg.exe";
static const wchar_t kMenuTitle[]    = L"Convert to JPEG";

// Windows caps a command line at 32767 characters. Large selections are split
// across several processes rather than being silently truncated.
static const size_t kMaxCommandLine = 30000;

static HMODULE g_module      = nullptr;
static LONG    g_objectCount = 0;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

// Full path to a file sitting next to this DLL. Under a sparse MSIX package the
// binaries live in the external location, so resolving relative to the module is
// the only reliable way to find the converter.
static std::wstring ModuleSiblingPath(const wchar_t* fileName)
{
    wchar_t buffer[MAX_PATH];
    DWORD length = GetModuleFileNameW(g_module, buffer, ARRAYSIZE(buffer));
    if (length == 0 || length == ARRAYSIZE(buffer))
    {
        return std::wstring();
    }

    wchar_t* lastSlash = wcsrchr(buffer, L'\\');
    if (!lastSlash)
    {
        return std::wstring();
    }

    *(lastSlash + 1) = L'\0';
    std::wstring path(buffer);
    path += fileName;
    return path;
}

static bool LaunchConverter(const std::wstring& exePath, const std::wstring& quotedArgs)
{
    std::wstring commandLine = L"\"";
    commandLine += exePath;
    commandLine += L"\"";
    commandLine += quotedArgs;

    // CreateProcessW may write to the command-line buffer, so it cannot be const.
    std::wstring mutableCommandLine = commandLine;

    STARTUPINFOW startupInfo = { sizeof(startupInfo) };
    PROCESS_INFORMATION processInfo = {};

    BOOL created = CreateProcessW(
        exePath.c_str(),
        &mutableCommandLine[0],
        nullptr,
        nullptr,
        FALSE,
        CREATE_NO_WINDOW,
        nullptr,
        nullptr,
        &startupInfo,
        &processInfo);

    if (!created)
    {
        return false;
    }

    // Do not wait: Invoke must return promptly or the context menu appears frozen.
    CloseHandle(processInfo.hThread);
    CloseHandle(processInfo.hProcess);
    return true;
}

// ---------------------------------------------------------------------------
// IExplorerCommand
// ---------------------------------------------------------------------------

class ConvertToJpegCommand : public IExplorerCommand
{
public:
    ConvertToJpegCommand() : m_refCount(1)
    {
        InterlockedIncrement(&g_objectCount);
    }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv)
        {
            return E_POINTER;
        }

        if (riid == IID_IUnknown || riid == IID_IExplorerCommand)
        {
            *ppv = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }

        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override
    {
        return InterlockedIncrement(&m_refCount);
    }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        LONG remaining = InterlockedDecrement(&m_refCount);
        if (remaining == 0)
        {
            delete this;
        }
        return remaining;
    }

    // IExplorerCommand
    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* ppszName) override
    {
        return SHStrDupW(kMenuTitle, ppszName);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* ppszIcon) override
    {
        // "<dll path>,-<resource id>": the negative form selects by resource ID
        // rather than by index, which stays correct if resources are reordered.
        wchar_t buffer[MAX_PATH];
        DWORD length = GetModuleFileNameW(g_module, buffer, MAX_PATH);
        if (length == 0 || length >= MAX_PATH)
        {
            return E_FAIL;
        }

        std::wstring icon(buffer);
        icon += L",-";
        icon += std::to_wstring(IDI_CONVERT);
        return SHStrDupW(icon.c_str(), ppszIcon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR*) override
    {
        // Returning E_NOTIMPL tells Explorer to show no tooltip, which is the
        // convention for verbs whose title is already self-explanatory.
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* pguidCommandName) override
    {
        if (!pguidCommandName)
        {
            return E_POINTER;
        }

        *pguidCommandName = CLSID_ConvertToJpeg;
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* pCmdState) override
    {
        if (!pCmdState)
        {
            return E_POINTER;
        }

        // The manifest already restricts this verb to .heic/.heif item types, so
        // the only thing left to check is that a selection actually exists.
        DWORD count = 0;
        if (!items || FAILED(items->GetCount(&count)) || count == 0)
        {
            *pCmdState = ECS_HIDDEN;
            return S_OK;
        }

        *pCmdState = ECS_ENABLED;
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* pFlags) override
    {
        if (!pFlags)
        {
            return E_POINTER;
        }

        *pFlags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand**) override
    {
        return E_NOTIMPL;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        if (!items)
        {
            return E_INVALIDARG;
        }

        DWORD count = 0;
        HRESULT hr = items->GetCount(&count);
        if (FAILED(hr))
        {
            return hr;
        }

        if (count == 0)
        {
            return S_OK;
        }

        std::wstring exePath = ModuleSiblingPath(kConverterExe);
        if (exePath.empty())
        {
            return E_FAIL;
        }

        std::wstring args;
        bool allLaunched = true;

        for (DWORD i = 0; i < count; ++i)
        {
            IShellItem* item = nullptr;
            if (FAILED(items->GetItemAt(i, &item)) || !item)
            {
                continue;
            }

            PWSTR filePath = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &filePath)) && filePath)
            {
                std::wstring argument = L" \"";
                argument += filePath;
                argument += L"\"";

                // Flush before overflowing, so a path is never split in half.
                if (args.size() + argument.size() + exePath.size() + 8 > kMaxCommandLine)
                {
                    allLaunched = LaunchConverter(exePath, args) && allLaunched;
                    args.clear();
                }

                args += argument;
                CoTaskMemFree(filePath);
            }

            item->Release();
        }

        if (!args.empty())
        {
            allLaunched = LaunchConverter(exePath, args) && allLaunched;
        }

        return allLaunched ? S_OK : E_FAIL;
    }

private:
    ~ConvertToJpegCommand()
    {
        InterlockedDecrement(&g_objectCount);
    }

    LONG m_refCount;
};

// ---------------------------------------------------------------------------
// IClassFactory
// ---------------------------------------------------------------------------

class ClassFactory : public IClassFactory
{
public:
    ClassFactory() : m_refCount(1)
    {
        InterlockedIncrement(&g_objectCount);
    }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv)
        {
            return E_POINTER;
        }

        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }

        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override
    {
        return InterlockedIncrement(&m_refCount);
    }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        LONG remaining = InterlockedDecrement(&m_refCount);
        if (remaining == 0)
        {
            delete this;
        }
        return remaining;
    }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv)
        {
            return E_POINTER;
        }

        *ppv = nullptr;
        if (outer)
        {
            return CLASS_E_NOAGGREGATION;
        }

        ConvertToJpegCommand* command = new (std::nothrow) ConvertToJpegCommand();
        if (!command)
        {
            return E_OUTOFMEMORY;
        }

        HRESULT hr = command->QueryInterface(riid, ppv);
        command->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock)
        {
            InterlockedIncrement(&g_objectCount);
        }
        else
        {
            InterlockedDecrement(&g_objectCount);
        }
        return S_OK;
    }

private:
    ~ClassFactory()
    {
        InterlockedDecrement(&g_objectCount);
    }

    LONG m_refCount;
};

// ---------------------------------------------------------------------------
// DLL exports
// ---------------------------------------------------------------------------

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (!ppv)
    {
        return E_POINTER;
    }

    *ppv = nullptr;
    if (rclsid != CLSID_ConvertToJpeg)
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    ClassFactory* factory = new (std::nothrow) ClassFactory();
    if (!factory)
    {
        return E_OUTOFMEMORY;
    }

    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return g_objectCount == 0 ? S_OK : S_FALSE;
}
