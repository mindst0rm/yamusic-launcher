cmake_minimum_required(VERSION 3.20)

project(AsarFusePatcher LANGUAGES CXX)

# C++20
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# ===================== DLL для P/Invoke =====================
add_library(AsarFusePatcher SHARED
        src/fuse_patcher_dll.cpp
)

target_include_directories(AsarFusePatcher PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include
)

# экспортный макрос для __declspec(dllexport)
target_compile_definitions(AsarFusePatcher PRIVATE ASFUSE_BUILD UNICODE _UNICODE)

# имя без префикса 'lib' (для MinGW) — получим AsarFusePatcher.dll
set_target_properties(AsarFusePatcher PROPERTIES
        OUTPUT_NAME "AsarFusePatcher"
        PREFIX ""
        IMPORT_PREFIX ""
)

if (MSVC)
    target_compile_options(AsarFusePatcher PRIVATE /utf-8)
endif()

# ---- MinGW: статическая линковка рантаймов libstdc++/libgcc ----
if (MINGW)
    target_link_options(AsarFusePatcher PRIVATE -static-libstdc++ -static-libgcc)

    get_filename_component(MINGW_BIN_DIR "${CMAKE_CXX_COMPILER}" DIRECTORY)
    set(MINGW_WINPTHREAD_DLL "${MINGW_BIN_DIR}/libwinpthread-1.dll")

    if (EXISTS "${MINGW_WINPTHREAD_DLL}")
        add_custom_command(TARGET AsarFusePatcher POST_BUILD
                COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${MINGW_WINPTHREAD_DLL}"
                "$<TARGET_FILE_DIR:AsarFusePatcher>/libwinpthread-1.dll")
    else()
        message(WARNING "libwinpthread-1.dll was not found near ${CMAKE_CXX_COMPILER}. The launcher installer may miss a required MinGW runtime dependency.")
    endif()
endif()
