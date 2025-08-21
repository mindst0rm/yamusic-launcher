#include "AsarFusePatcher.h"

#include <cstdint>
#include <vector>
#include <string>
#include <filesystem>
#include <fstream>
#include <stdexcept>
#include <cstring>

using bytes = std::vector<std::uint8_t>;

namespace detail {

struct Sec { DWORD va, vsize, raw, rsize; };
struct PE  { bool x64{}; std::vector<Sec> secs; };

static bytes read_all(const std::filesystem::path& p) {
    std::ifstream f(p, std::ios::binary);
    if (!f) throw std::runtime_error("open failed: " + p.string());
    return bytes(std::istreambuf_iterator<char>(f), {});
}
static void write_all(const std::filesystem::path& p, const bytes& d) {
    std::ofstream f(p, std::ios::binary | std::ios::trunc);
    if (!f) throw std::runtime_error("write failed: " + p.string());
    f.write(reinterpret_cast<const char*>(d.data()), (std::streamsize)d.size());
}

static PE parse_pe(const bytes& img) {
    if (img.size() < sizeof(IMAGE_DOS_HEADER)) throw std::runtime_error("too small (no MZ)");
    auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(img.data());
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) throw std::runtime_error("bad MZ");

    auto ntAny = img.data() + dos->e_lfanew;
    if (img.size() < (size_t)dos->e_lfanew + sizeof(DWORD) + sizeof(IMAGE_FILE_HEADER))
        throw std::runtime_error("bad e_lfanew");
    if (*reinterpret_cast<const DWORD*>(ntAny) != IMAGE_NT_SIGNATURE)
        throw std::runtime_error("bad PE sig");

    auto optMagic = *reinterpret_cast<const WORD*>(ntAny + sizeof(DWORD) + sizeof(IMAGE_FILE_HEADER));
    bool is64 = (optMagic == IMAGE_NT_OPTIONAL_HDR64_MAGIC);

    PE pe{}; pe.x64 = is64;

    if (is64) {
        auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(ntAny);
        auto sec = IMAGE_FIRST_SECTION(nt);
        for (unsigned i=0; i<nt->FileHeader.NumberOfSections; ++i, ++sec) {
            Sec s{ sec->VirtualAddress,
                   std::max(sec->Misc.VirtualSize, sec->SizeOfRawData),
                   sec->PointerToRawData,
                   sec->SizeOfRawData };
            pe.secs.push_back(s);
        }
    } else {
        auto nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(ntAny);
        auto sec = IMAGE_FIRST_SECTION(nt);
        for (unsigned i=0; i<nt->FileHeader.NumberOfSections; ++i, ++sec) {
            Sec s{ sec->VirtualAddress,
                   std::max(sec->Misc.VirtualSize, sec->SizeOfRawData),
                   sec->PointerToRawData,
                   sec->SizeOfRawData };
            pe.secs.push_back(s);
        }
    }
    return pe;
}

static size_t rva_to_off(const PE& pe, DWORD rva) {
    for (const auto& s : pe.secs) {
        DWORD end = s.va + s.rsize;
        if (rva >= s.va && rva < end) return (size_t)(s.raw + (rva - s.va));
    }
    throw std::runtime_error("RVA not mapped to raw data");
}
static DWORD off_to_rva(const PE& pe, size_t off) {
    for (const auto& s : pe.secs) {
        size_t end = s.raw + s.rsize;
        if (off >= s.raw && off < end) return s.va + (DWORD)(off - s.raw);
    }
    throw std::runtime_error("offset not in raw data");
}

struct Hit { size_t mov_off; DWORD target_rva; size_t target_off; uint8_t cur; };

static std::vector<Hit> scan_inline_fuse_bools(const PE& pe, const bytes& img) {
    // Ищем: 8A 05 <rel32> 3C 31 0F 94 C0 C3
    const uint8_t tail[] = {0x3C,0x31, 0x0F,0x94,0xC0, 0xC3};
    std::vector<Hit> hits;

    for (const auto& s : pe.secs) {
        size_t start = s.raw, end = s.raw + s.rsize;
        if (end > img.size()) continue;

        for (size_t i = start; i + 2 + 4 + sizeof(tail) <= end; ++i) {
            if (img[i] != 0x8A || img[i+1] != 0x05) continue; // mov al, [rip+...]
            bool tailok = true;
            for (size_t k=0; k<sizeof(tail); ++k) if (img[i+6+k] != tail[k]) { tailok = false; break; }
            if (!tailok) continue;

            int32_t disp = *reinterpret_cast<const int32_t*>(&img[i+2]);
            DWORD rva_mov  = off_to_rva(pe, i);
            DWORD rva_next = rva_mov + 6; // RIP — следующий байт после rel32
            DWORD target_rva = (DWORD)((int64_t)rva_next + disp);
            size_t target_off;
            try { target_off = rva_to_off(pe, target_rva); }
            catch (...) { continue; }

            uint8_t cur = img[target_off];
            if (cur == '0' || cur == '1') {
                hits.push_back({ i, target_rva, target_off, cur });
            }
        }
    }
    return hits;
}

static void copy_err(wchar_t* buf, int cap, const std::wstring& msg) {
    if (!buf || cap <= 0) return;
    int n = (int)msg.size();
    if (n >= cap) n = cap - 1;
    std::wmemcpy(buf, msg.c_str(), n);
    buf[n] = L'\0';
}

} // namespace detail

extern "C" ASFUSE_API int WINAPI DisableAsarIntegrityFuse(
    const wchar_t* exePath,
    BOOL dryRun,
    int limit,
    wchar_t* errBuf,
    int errBufChars
) // <-- БЕЗ noexcept, чтобы совпадало с .h
{
    using namespace detail;
    try {
        if (!exePath || *exePath == L'\0') {
            copy_err(errBuf, errBufChars, L"invalid exePath");
            return ASFUSE_E_ARGS;
        }
        std::filesystem::path exe = exePath;
        if (!std::filesystem::exists(exe)) {
            copy_err(errBuf, errBufChars, L"file not found");
            return ASFUSE_E_ARGS;
        }

        bytes orig = read_all(exe);
        PE pe = parse_pe(orig);

        auto hits = scan_inline_fuse_bools(pe, orig);
        if (hits.empty()) {
            copy_err(errBuf, errBufChars, L"no inline fuse patterns found");
            return 0;
        }

        int wouldChange = 0;
        for (auto& h : hits) if (h.cur == '1') ++wouldChange;

        if (dryRun || limit == 0 || wouldChange == 0) {
            return wouldChange;
        }

        bytes mod = orig;
        int patched = 0;
        for (const auto& h : hits) {
            if (h.cur != '1') continue;
            if (limit >= 0 && patched >= limit) break;
            mod[h.target_off] = '0';
            ++patched;
        }
        if (patched == 0) return 0;

        auto bak = exe; bak += L".fuses.bak";
        if (!std::filesystem::exists(bak)) {
            write_all(bak, orig);
        }
        write_all(exe, mod);

        return patched;

    } catch (const std::ios_base::failure&) {
        copy_err(errBuf, errBufChars, L"I/O error");
        return ASFUSE_E_IO;
    } catch (const std::runtime_error& e) {
        std::wstring wmsg(e.what(), e.what() + std::strlen(e.what()));
        if (wmsg.find(L"PE") != std::wstring::npos) {
            copy_err(errBuf, errBufChars, L"PE parse error");
            return ASFUSE_E_PE;
        }
        copy_err(errBuf, errBufChars, wmsg);
        return ASFUSE_E_FAIL;
    } catch (...) {
        copy_err(errBuf, errBufChars, L"unknown error");
        return ASFUSE_E_FAIL;
    }
}
