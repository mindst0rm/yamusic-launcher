#pragma once
#include <windows.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifndef ASFUSE_API
#  ifdef ASFUSE_BUILD
#    define ASFUSE_API __declspec(dllexport)
#  else
#    define ASFUSE_API __declspec(dllimport)
#  endif
#endif

    // Коды ошибок (отрицательные)
#define ASFUSE_E_ARGS   (-1)  // неверные аргументы/путь
#define ASFUSE_E_IO     (-2)  // ошибка ввода-вывода
#define ASFUSE_E_PE     (-3)  // ошибка парсинга PE
#define ASFUSE_E_FAIL   (-4)  // прочая ошибка

    // Отключение inline fuse-проверок ('1' -> '0') в бинаре Electron
    // exePath     — путь к exe (UTF-16)
    // dryRun      — TRUE: только посчитать, сколько БЫ изменили; FALSE: применить
    // limit       — максимум патчей; -1 = без лимита; 0 = ничего не менять
    // errBuf      — буфер под сообщение об ошибке (может быть NULL)
    // errBufChars — размер буфера в wchar_t
    ASFUSE_API int WINAPI DisableAsarIntegrityFuse(
        const wchar_t* exePath,
        BOOL dryRun,
        int limit,
        wchar_t* errBuf,
        int errBufChars
    );

#ifdef __cplusplus
}
#endif
