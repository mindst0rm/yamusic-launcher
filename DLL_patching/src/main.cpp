#include <windows.h>
#include <bcrypt.h>
#pragma comment(lib, "Bcrypt.lib")

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>
#include <fstream>
#include <iostream>
#include <iomanip>
#include <filesystem>
#include <stdexcept>
#include <optional>

using bytes = std::vector<std::uint8_t>;

// ---------------- helpers ----------------
static std::wstring trim_quotes(std::wstring s){
    if(!s.empty() && s.front()==L'"') s.erase(s.begin());
    if(!s.empty() && s.back()==L'"')  s.pop_back();
    return s;
}
static std::string utf8_from_wide(std::wstring_view w){
    if(w.empty()) return {};
    int need = WideCharToMultiByte(CP_UTF8,0,w.data(),(int)w.size(),nullptr,0,nullptr,nullptr);
    if(need<=0) throw std::runtime_error("WideCharToMultiByte size failed");
    std::string s(need,'\0');
    if(!WideCharToMultiByte(CP_UTF8,0,w.data(),(int)w.size(),s.data(),need,nullptr,nullptr))
        throw std::runtime_error("WideCharToMultiByte failed");
    return s;
}
static std::string json_escape(std::string_view s){
    std::string out; out.reserve(s.size()+8);
    for(unsigned char c: s){
        switch(c){
            case '\\': out+="\\\\"; break;
            case '\"': out+="\\\""; break;
            case '\b': out+="\\b";  break;
            case '\f': out+="\\f";  break;
            case '\n': out+="\\n";  break;
            case '\r': out+="\\r";  break;
            case '\t': out+="\\t";  break;
            default:
                if(c<0x20){ char buf[7]; std::snprintf(buf,sizeof(buf),"\\u%04X",c); out+=buf; }
                else out.push_back((char)c);
        }
    }
    return out;
}
static void hexdump_prefix(const void* data,size_t size,size_t max=64){
    auto* p = static_cast<const unsigned char*>(data);
    size_t n = std::min(size,max);
    for(size_t i=0;i<n;++i){
        if(i && i%16==0) std::wcout<<L"\n";
        std::wcout<<std::hex<<std::setw(2)<<std::setfill(L'0')<<(int)p[i]<<L' ';
    }
    std::wcout<<std::dec<<L"\n";
}
static bytes read_file(const std::filesystem::path& p){
    std::ifstream f(p,std::ios::binary);
    if(!f) throw std::runtime_error("open failed: "+p.string());
    return bytes(std::istreambuf_iterator<char>(f),{});
}
static void write_file(const std::filesystem::path& p,const bytes& data){
    std::ofstream f(p,std::ios::binary|std::ios::trunc);
    if(!f) throw std::runtime_error("write failed: "+p.string());
    f.write(reinterpret_cast<const char*>(data.data()),(std::streamsize)data.size());
}
static std::wstring to_lower_hex(const bytes& b){
    static const wchar_t* hexd=L"0123456789abcdef";
    std::wstring s; s.resize(b.size()*2);
    for(size_t i=0;i<b.size();++i){ s[i*2]=hexd[(b[i]>>4)&0xF]; s[i*2+1]=hexd[b[i]&0xF]; }
    return s;
}
static bytes sha256(const bytes& data){
    BCRYPT_ALG_HANDLE hAlg{}; BCRYPT_HASH_HANDLE hHash{};
    DWORD objLen=0,cb=0;
    if(BCryptOpenAlgorithmProvider(&hAlg,BCRYPT_SHA256_ALGORITHM,nullptr,0))
        throw std::runtime_error("BCryptOpenAlgorithmProvider failed");
    if(BCryptGetProperty(hAlg,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&objLen),sizeof(objLen),&cb,0)){
        BCryptCloseAlgorithmProvider(hAlg,0); throw std::runtime_error("BCRYPT_OBJECT_LENGTH failed");
    }
    std::vector<BYTE> obj(objLen);
    DWORD hashLen=0;
    if(BCryptGetProperty(hAlg,BCRYPT_HASH_LENGTH,reinterpret_cast<PUCHAR>(&hashLen),sizeof(hashLen),&cb,0)){
        BCryptCloseAlgorithmProvider(hAlg,0); throw std::runtime_error("BCRYPT_HASH_LENGTH failed");
    }
    bytes dig(hashLen);
    if(BCryptCreateHash(hAlg,&hHash,obj.data(),objLen,nullptr,0,0)){
        BCryptCloseAlgorithmProvider(hAlg,0); throw std::runtime_error("BCryptCreateHash failed");
    }
    if(!data.empty()){
        const BYTE* pc = reinterpret_cast<const BYTE*>(data.data());
        BYTE* p = const_cast<BYTE*>(pc);
        if(BCryptHashData(hHash,p,(ULONG)data.size(),0)){
            BCryptDestroyHash(hHash); BCryptCloseAlgorithmProvider(hAlg,0);
            throw std::runtime_error("BCryptHashData failed");
        }
    }
    if(BCryptFinishHash(hHash,dig.data(),(ULONG)dig.size(),0)){
        BCryptDestroyHash(hHash); BCryptCloseAlgorithmProvider(hAlg,0); throw std::runtime_error("BCryptFinishHash failed");
    }
    BCryptDestroyHash(hHash); BCryptCloseAlgorithmProvider(hAlg,0);
    return dig;
}
static bool is_hex64(std::string_view s){
    if(s.size()!=64) return false;
    for(char c: s){
        if(!(c>='0'&&c<='9') && !(c>='a'&&c<='f') && !(c>='A'&&c<='F')) return false;
    }
    return true;
}

// ---------------- ASAR ----------------
// читаем: [u32 header_size][header (header_size bytes)]
struct AsarParts { bytes header_only; bytes header_block; };

static AsarParts read_asar_headers(const std::filesystem::path& asar){
    std::ifstream f(asar,std::ios::binary);
    if(!f) throw std::runtime_error("open failed: "+asar.string());
    uint32_t header_size=0;
    if(!f.read(reinterpret_cast<char*>(&header_size),4))
        throw std::runtime_error("read header_size failed");
    if(header_size==0 || header_size>32u*1024u*1024u)
        throw std::runtime_error("unreasonable header_size");

    AsarParts ap;
    ap.header_only.resize(header_size);
    if(!f.read(reinterpret_cast<char*>(ap.header_only.data()),header_size))
        throw std::runtime_error("read header bytes failed");

    ap.header_block.resize(4+header_size);
    std::memcpy(ap.header_block.data(),&header_size,4);
    std::memcpy(ap.header_block.data()+4,ap.header_only.data(),header_size);
    return ap;
}

// ---------------- read current resource ----------------
struct IntegrityInfo { std::string json, file_field, value_hex; WORD lang=0; };

static BOOL CALLBACK EnumLangProc(HMODULE, LPCWSTR, LPCWSTR, WORD lang, LONG_PTR param){
    *reinterpret_cast<WORD*>(param)=lang; return FALSE; // возьмём первый
}

static std::optional<IntegrityInfo> read_integrity_resource(const std::filesystem::path& exe){
    HMODULE mod = LoadLibraryExW(exe.c_str(),nullptr,LOAD_LIBRARY_AS_DATAFILE|LOAD_LIBRARY_AS_IMAGE_RESOURCE);
    if(!mod) return std::nullopt;

    WORD lang=0;
    EnumResourceLanguagesW(mod,L"Integrity",L"ElectronAsar",EnumLangProc,reinterpret_cast<LONG_PTR>(&lang));

    HRSRC res = FindResourceExW(mod,L"Integrity",L"ElectronAsar",lang?lang:MAKELANGID(LANG_NEUTRAL,SUBLANG_NEUTRAL));
    if(!res){ FreeLibrary(mod); return std::nullopt; }

    DWORD sz = SizeofResource(mod,res);
    HGLOBAL hg = LoadResource(mod,res);
    if(!hg){ FreeLibrary(mod); return std::nullopt; }
    void* ptr = LockResource(hg);
    if(!ptr || sz==0){ FreeLibrary(mod); return std::nullopt; }

    IntegrityInfo info; info.lang = lang;
    info.json.assign(reinterpret_cast<const char*>(ptr),reinterpret_cast<const char*>(ptr)+sz);
    FreeLibrary(mod);

    auto find_field = [&](const char* key)->std::string{
        std::string k = std::string("\"")+key+"\":\"";
        size_t p = info.json.find(k);
        if(p==std::string::npos) return {};
        p += k.size();
        size_t q = info.json.find('\"',p);
        if(q==std::string::npos) return {};
        return info.json.substr(p,q-p);
    };
    info.file_field = find_field("file");
    info.value_hex  = find_field("value");
    return info;
}

static WORD pick_lang_for_write(const std::filesystem::path& exe){
    if(auto cur = read_integrity_resource(exe)){
        if(cur->lang) return cur->lang;
    }
    return 1033; // en-US как у Electron
}

// ---------------- write resource ----------------
static void write_integrity_resource(const std::filesystem::path& exe,
                                     const std::wstring& fileFieldW,
                                     const std::wstring& hexHash,
                                     bool dryRun)
{
    std::string fileUtf8 = utf8_from_wide(fileFieldW);
    std::string json;
    json += "[{\"file\":\"";
    json += json_escape(fileUtf8);
    json += "\",\"alg\":\"SHA256\",\"value\":\""; // UPPERCASE, как в ресурсах
    json += std::string(hexHash.begin(),hexHash.end());
    json += "\"}]";

    std::wcout<<L"JSON to write ("<<json.size()<<L" bytes):\n"
              <<std::wstring(json.begin(),json.end())<<L"\n";

    if(dryRun) return;

    WORD lang = pick_lang_for_write(exe);
    HANDLE h = BeginUpdateResourceW(exe.c_str(),FALSE);
    if(!h) throw std::runtime_error("BeginUpdateResourceW failed");
    BOOL ok = UpdateResourceW(h,L"Integrity",L"ElectronAsar",lang,(void*)json.data(),(DWORD)json.size());
    if(!ok){ EndUpdateResourceW(h,TRUE); throw std::runtime_error("UpdateResourceW failed"); }
    if(!EndUpdateResourceW(h,FALSE)) throw std::runtime_error("EndUpdateResourceW failed");
}

// ---------------- entry ----------------
int wmain(int argc, wchar_t* argv[]) try {
    if(argc<2){
        std::wcerr<<L"Usage:\n  "<<argv[0]
            <<L" <YourApp.exe> [--asar <path\\to\\app.asar>] [--file-field \"resources\\\\app.asar\"]\n"
            <<L"               [--mode header|block] [--force-hash <64hex>] [--auto-force-hash] [--dry-run]\n";
        return 1;
    }

    std::filesystem::path exe = trim_quotes(argv[1]);
    std::filesystem::path asar;
    std::wstring fileField = L"resources\\app.asar";
    std::wstring mode = L"block"; // чаще совпадает
    std::optional<std::string> forceHash;
    bool autoForceHash = false;
    bool dry=false;

    for(int i=2;i<argc;++i){
        std::wstring a = argv[i];
        if(a==L"--asar" && i+1<argc)       asar = trim_quotes(argv[++i]);
        else if(a==L"--file-field" && i+1<argc) fileField = trim_quotes(argv[++i]);
        else if(a==L"--mode" && i+1<argc)  mode = trim_quotes(argv[++i]);
        else if(a==L"--force-hash" && i+1<argc){
            std::wstring w = trim_quotes(argv[++i]); std::string s = utf8_from_wide(w);
            if(!is_hex64(s)) throw std::runtime_error("--force-hash must be 64 hex chars");
            for(char& c: s) if(c>='A'&&c<='F') c = (char)(c-'A'+'a');
            forceHash = s;
        }
        else if(a==L"--auto-force-hash") autoForceHash = true;
        else if(a==L"--dry-run") dry = true;
    }
    if(asar.empty()) asar = exe.parent_path()/ "resources" / "app.asar";

    std::wcout<<L"EXE : "<<exe.wstring()<<L"\n";
    std::wcout<<L"ASAR: "<<asar.wstring()<<L"\n";
    std::wcout<<L"file field -> "<<fileField<<L"\n";

    if(!std::filesystem::exists(exe)){ std::wcerr<<L"EXE not found\n"; return 2; }

    // Проверим конфликт параметров
    if(forceHash && autoForceHash){
        throw std::runtime_error("Cannot use both --force-hash and --auto-force-hash at the same time");
    }

    // покажем текущий ресурс
    std::optional<IntegrityInfo> currentResource;
    if(auto cur=read_integrity_resource(exe)){
        currentResource = cur;
        std::wcout<<L"Current resource size: "<<cur->json.size()<<L" bytes\n";
        hexdump_prefix(cur->json.data(),cur->json.size());
        std::wcout<<L"Current JSON: "<<std::wstring(cur->json.begin(),cur->json.end())<<L"\n";
        if(!cur->file_field.empty()) std::wcout<<L"  file  = "<<std::wstring(cur->file_field.begin(),cur->file_field.end())<<L"\n";
        if(!cur->value_hex.empty())  std::wcout<<L"  value = "<<std::wstring(cur->value_hex.begin(),cur->value_hex.end())<<L"\n";
    }else{
        std::wcout<<L"Current resource not found (will create).\n";
    }

    // бэкап EXE
    auto bak = exe; bak += L".bak";
    if(!std::filesystem::exists(bak)){ write_file(bak,read_file(exe)); std::wcout<<L"Backup created: "<<bak<<L"\n"; }
    else std::wcout<<L"Backup exists : "<<bak<<L"\n";

    std::wstring hex;
    if(autoForceHash){
        // используем хеш из текущего ресурса
        if(!currentResource || currentResource->value_hex.empty()){
            throw std::runtime_error("--auto-force-hash specified but no current hash found in EXE resource");
        }
        std::string currentHash = currentResource->value_hex;
        // проверим что это валидный хеш
        if(!is_hex64(currentHash)){
            throw std::runtime_error("Current hash in EXE resource is not valid 64-char hex");
        }
        // приводим к нижнему регистру
        for(char& c: currentHash) {
            if(c>='A'&&c<='F') c = (char)(c-'A'+'a');
        }
        hex = std::wstring(currentHash.begin(), currentHash.end());
        std::wcout<<L"Using --auto-force-hash from current resource: "<<hex<<L"\n";
    }
    else if(forceHash){
        // подсунуть «правильный» хеш (например, тот, что показал краш Electron как actual)
        std::string s = *forceHash;
        hex.assign(s.begin(), s.end());
        std::wcout<<L"Using --force-hash: "<<hex<<L"\n";
    }else{
        if(!std::filesystem::exists(asar)){ std::wcerr<<L"ASAR not found\n"; return 2; }
        AsarParts ap = read_asar_headers(asar);
        const std::wstring hex_header = to_lower_hex(sha256(ap.header_only));
        const std::wstring hex_block  = to_lower_hex(sha256(ap.header_block));
        std::wcout<<L"header SHA-256 : "<<hex_header<<L"\n";
        std::wcout<<L"block  SHA-256 : "<<hex_block <<L"\n";
        hex = (mode==L"header") ? hex_header : hex_block;
        std::wcout<<L"-> chosen      : "<<hex<<L"\n";
    }

    // запись ресурса
    write_integrity_resource(exe,fileField,hex,dry);
    std::wcout<<(dry?L"[dry-run] Done.\n":L"Done.\n");
    return 0;

} catch(const std::exception& e){
    std::cerr<<"ERROR: "<<e.what()<<"\n";
    return 1;
}