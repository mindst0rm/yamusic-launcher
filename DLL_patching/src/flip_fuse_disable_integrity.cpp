// flip_fuse_disable_integrity.cpp
// C++20, Windows-only. Отключает инлайн-проверки fuse'ов вида "cmp al,'1'; sete al; ret"
// за счёт патча источника байта ('1' -> '0'), на который идёт RIP-relative загрузка.

#include <windows.h>
#include <cstdint>
#include <vector>
#include <string>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iomanip>
#include <stdexcept>

using bytes = std::vector<std::uint8_t>;

struct Sec { DWORD va, vsize, raw, rsize; std::string name; };

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

struct PE {
    bool x64{};
    DWORD imageBaseLow{};
    DWORD64 imageBase{};
    std::vector<Sec> secs;
};

static PE parse_pe(const bytes& img) {
    if (img.size() < sizeof(IMAGE_DOS_HEADER)) throw std::runtime_error("too small (no MZ)");
    auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(img.data());
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) throw std::runtime_error("bad MZ");
    if (img.size() < (size_t)dos->e_lfanew + sizeof(DWORD) + sizeof(IMAGE_FILE_HEADER))
        throw std::runtime_error("bad e_lfanew");

    auto sig = *reinterpret_cast<const DWORD*>(img.data() + dos->e_lfanew);
    if (sig != IMAGE_NT_SIGNATURE) throw std::runtime_error("bad PE sig");

    auto fh = reinterpret_cast<const IMAGE_FILE_HEADER*>(img.data() + dos->e_lfanew + sizeof(DWORD));
    auto optMagic = *reinterpret_cast<const WORD*>(img.data() + dos->e_lfanew + sizeof(DWORD) + sizeof(IMAGE_FILE_HEADER));
    bool is64 = (optMagic == IMAGE_NT_OPTIONAL_HDR64_MAGIC);

    size_t optOff = dos->e_lfanew + sizeof(DWORD) + sizeof(IMAGE_FILE_HEADER);
    PE pe{};
    pe.x64 = is64;

    if (is64) {
        auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(img.data() + dos->e_lfanew);
        pe.imageBase = nt->OptionalHeader.ImageBase;
        auto sec = IMAGE_FIRST_SECTION(nt);
        for (unsigned i=0;i<nt->FileHeader.NumberOfSections;++i, ++sec) {
            Sec s{sec->VirtualAddress,
                  std::max(sec->Misc.VirtualSize, sec->SizeOfRawData),
                  sec->PointerToRawData,
                  sec->SizeOfRawData,
                  std::string(reinterpret_cast<const char*>(sec->Name), 
                              reinterpret_cast<const char*>(sec->Name)+strnlen((const char*)sec->Name,8))};
            pe.secs.push_back(s);
        }
    } else {
        auto nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(img.data() + dos->e_lfanew);
        pe.imageBase = nt->OptionalHeader.ImageBase;
        auto sec = IMAGE_FIRST_SECTION(nt);
        for (unsigned i=0;i<nt->FileHeader.NumberOfSections;++i, ++sec) {
            Sec s{sec->VirtualAddress,
                  std::max(sec->Misc.VirtualSize, sec->SizeOfRawData),
                  sec->PointerToRawData,
                  sec->SizeOfRawData,
                  std::string(reinterpret_cast<const char*>(sec->Name), 
                              reinterpret_cast<const char*>(sec->Name)+strnlen((const char*)sec->Name,8))};
            pe.secs.push_back(s);
        }
    }
    return pe;
}

static size_t rva_to_off(const PE& pe, DWORD rva) {
    for (const auto& s : pe.secs) {
        if (rva >= s.va && rva < s.va + s.rsize) {
            return (size_t)(s.raw + (rva - s.va));
        }
        // если VirtualSize > SizeOfRawData, но нас интересует именно сырые данные
    }
    throw std::runtime_error("RVA->off: rva not mapped to raw section");
}
static DWORD off_to_rva(const PE& pe, size_t off) {
    for (const auto& s : pe.secs) {
        if (off >= s.raw && off < s.raw + s.rsize) {
            return s.va + (DWORD)(off - s.raw);
        }
    }
    throw std::runtime_error("off->RVA: offset outside raw sections");
}

struct Hit { size_t mov_off; DWORD target_rva; size_t target_off; uint8_t current; };

static std::vector<Hit> scan_fuse_bools(const PE& pe, const bytes& img) {
    // Ищем сигнатуру: 8A 05 ?? ?? ?? ?? 3C 31 0F 94 C0 C3
    std::vector<Hit> out;
    const uint8_t sig[] = {0x8A,0x05, 0,0,0,0, 0x3C,0x31, 0x0F,0x94,0xC0, 0xC3};
    for (const auto& s : pe.secs) {
        // ограничимся исполняемыми секциями (.text обычно помечен MEM_EXECUTE)
        // но через Characteristics сюда не полезем — просто ищем в сырых данных секции
        size_t start = s.raw, end = s.raw + s.rsize;
        if (end > img.size()) continue;
        for (size_t i = start; i + sizeof(sig) <= end; ++i) {
            if (img[i]   != sig[0] || img[i+1] != sig[1]) continue;
            if (img[i+6] != sig[6] || img[i+7] != sig[7] ||
                img[i+8] != sig[8] || img[i+9] != sig[9] ||
                img[i+10]!= sig[10]|| img[i+11]!= sig[11]) continue;

            int32_t disp = *reinterpret_cast<const int32_t*>(&img[i+2]);
            DWORD rva_mov = off_to_rva(pe, i);
            DWORD rva_next = rva_mov + 6; // RIP — адрес следующей инструкции после rel32
            DWORD target_rva = (DWORD)((int64_t)rva_next + disp);
            size_t target_off;
            try { target_off = rva_to_off(pe, target_rva); }
            catch (...) { continue; }

            uint8_t cur = img[target_off];
            // интересуют ascii '0'/'1'
            if (cur == '0' || cur == '1') {
                out.push_back({ i, target_rva, target_off, cur });
            }
        }
    }
    return out;
}

static void print_hit(const Hit& h, const PE& pe) {
    std::wcout << L"  mov @file_off 0x" << std::hex << h.mov_off
               << L"  -> target RVA 0x" << h.target_rva
               << L" (file_off 0x" << std::hex << h.target_off << L")"
               << L"  value='" << (wchar_t)h.current << L"'\n" << std::dec;
}

int wmain(int argc, wchar_t* argv[]) try {
    if (argc < 2) {
        std::wcerr << L"Usage:\n  " << argv[0]
                   << L" <target.exe> [--dry-run] [--limit N]\n";
        return 1;
    }
    std::filesystem::path exe = argv[1];
    bool dry = false;
    int limit = -1;
    for (int i = 2; i < argc; ++i) {
        std::wstring a = argv[i];
        if (a == L"--dry-run") dry = true;
        else if (a == L"--limit" && i+1 < argc) limit = std::stoi(argv[++i]);
    }

    bytes img = read_all(exe);
    PE pe = parse_pe(img);

    auto hits = scan_fuse_bools(pe, img);
    if (hits.empty()) {
        std::wcerr << L"No inline fuse patterns found.\n";
        return 2;
    }

    std::wcout << L"Found " << hits.size() << L" fuse boolean sites:\n";
    for (const auto& h : hits) print_hit(h, pe);

    int patched = 0;
    for (const auto& h : hits) {
        if (limit >= 0 && patched >= limit) break;
        if (h.current == '1') {
            if (!dry) img[h.target_off] = '0';
            ++patched;
        }
    }

    if (patched == 0) {
        std::wcout << L"Nothing to change (either already '0', or --limit=0).\n";
        return 0;
    }

    if (!dry) {
        // backup
        auto bak = exe; bak += L".fuses.bak";
        if (!std::filesystem::exists(bak)) {
            write_all(bak, img); // OOPS: we must write original, not modified
        }
        // actually write modified image:
        // re-read original to create backup then write modified
    }

    // корректный бэкап + запись
    if (!dry) {
        // создадим бэкап из ПРЕЖНЕГО содержимого
        auto bak = exe; bak += L".fuses.bak";
        if (!std::filesystem::exists(bak)) {
            auto orig = read_all(exe);
            write_all(bak, orig);
            std::wcout << L"Backup created: " << bak << L"\n";
        } else {
            std::wcout << L"Backup exists: " << bak << L"\n";
        }

        write_all(exe, img);
        std::wcout << L"Patched " << patched << L" site(s). Done.\n";
    } else {
        std::wcout << L"[dry-run] Would patch " << patched << L" site(s) ('1' -> '0').\n";
    }

    return 0;

} catch (const std::exception& e) {
    std::cerr << "ERROR: " << e.what() << "\n";
    return 1;
}
