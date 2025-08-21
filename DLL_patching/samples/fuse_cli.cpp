#include "AsarFusePatcher.h"
#include <iostream>

int wmain(int argc, wchar_t* argv[]) {
    if (argc < 2) {
        std::wcout << L"Usage: fuse_disable_cli <target.exe> [--dry-run] [--limit N]\n";
        return 1;
    }
    const wchar_t* path = argv[1];
    BOOL dry = FALSE;
    int  limit = -1;
    for (int i=2; i<argc; ++i) {
        if (wcscmp(argv[i], L"--dry-run") == 0) dry = TRUE;
        else if (wcscmp(argv[i], L"--limit") == 0 && i+1 < argc) {
            limit = _wtoi(argv[++i]);
        }
    }

    wchar_t err[512] = {};
    int rc = DisableAsarIntegrityFuse(path, dry, limit, err, 512);
    if (rc < 0) {
        std::wcout << L"Error (" << rc << L"): " << err << L"\n";
        return 2;
    }
    if (dry) std::wcout << L"Would patch: " << rc << L" site(s)\n";
    else     std::wcout << L"Patched    : " << rc << L" site(s)\n";
    return 0;
}
