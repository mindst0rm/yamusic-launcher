cmake_minimum_required(VERSION 3.20)

project(patching_ya_music LANGUAGES CXX)

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
# уменьшает число внешних DLL (libstdc++-6.dll, libgcc_s_seh-1.dll)
# Примечание: libwinpthread-1.dll обычно остаётся динамической.
if (MINGW)
    target_link_options(AsarFusePatcher PRIVATE -static-libstdc++ -static-libgcc)
    # Если используете потоковую библиотеку MinGW, можно попробовать статич. pthread,
    # но полностью -static иногда ломает загрузку плагинов, поэтому по умолчанию — НЕ включаем.
    # target_link_options(AsarFusePatcher PRIVATE -Wl,--as-needed)
endif()

# ===================== опциональные exe =====================
if (EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/src/main.cpp")
    add_executable(patching_ya_music
            src/main.cpp
    )
    set_target_properties(patching_ya_music PROPERTIES WIN32_EXECUTABLE OFF)
    if (MINGW)
        target_link_options(patching_ya_music PRIVATE -municode)
    endif()
    target_link_libraries(patching_ya_music PRIVATE bcrypt)
    if (MSVC)
        target_compile_options(patching_ya_music PRIVATE /utf-8)
    endif()
endif()

if (EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/src/flip_fuse_disable_integrity.cpp")
    add_executable(fuse_disable
            src/flip_fuse_disable_integrity.cpp
    )
    set_target_properties(fuse_disable PROPERTIES WIN32_EXECUTABLE OFF)
    if (MINGW)
        target_link_options(fuse_disable PRIVATE -municode)
    endif()
    if (MSVC)
        target_compile_options(fuse_disable PRIVATE /utf-8)
    endif()
endif()

# ===================== samples / fuse_cli =====================
if (EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/samples/fuse_cli.cpp")
    add_executable(fuse_cli
            samples/fuse_cli.cpp
    )
    set_target_properties(fuse_cli PROPERTIES WIN32_EXECUTABLE OFF)
    target_include_directories(fuse_cli PRIVATE
            ${CMAKE_CURRENT_SOURCE_DIR}/include
    )
    target_link_libraries(fuse_cli PRIVATE AsarFusePatcher)
    if (MINGW)
        target_link_options(fuse_cli PRIVATE -municode)
    endif()
    if (MSVC)
        target_compile_options(fuse_cli PRIVATE /utf-8)
    endif()

    # Положим DLL рядом с fuse_cli.exe
    add_custom_command(TARGET fuse_cli POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:AsarFusePatcher>"
            "$<TARGET_FILE_DIR:fuse_cli>/"
            VERBATIM
    )
endif()

# ===================== MinGW: подложить зависимости рядом (если нужны) =====================
# Если несмотря на -static-libstdc++/-static-libgcc загрузчик всё ещё требует MinGW DLL,
# скопируем их рядом с AsarFusePatcher.dll (только если реально существуют).
if (MINGW)
    get_filename_component(_MINGW_BIN_DIR "${CMAKE_CXX_COMPILER}" DIRECTORY)
    set(_MINGW_DEPS
            "${_MINGW_BIN_DIR}/libstdc++-6.dll"
            "${_MINGW_BIN_DIR}/libgcc_s_seh-1.dll"
            "${_MINGW_BIN_DIR}/libwinpthread-1.dll"
    )
    foreach(_dll IN LISTS _MINGW_DEPS)
        if (EXISTS "${_dll}")
            add_custom_command(TARGET AsarFusePatcher POST_BUILD
                    COMMAND ${CMAKE_COMMAND} -E copy_if_different
                    "${_dll}" "$<TARGET_FILE_DIR:AsarFusePatcher>/"
                    VERBATIM
            )
        endif()
    endforeach()
endif()

# ===================== совместимость со старым таргетом =====================
if (NOT TARGET patching_ya_music)
    add_custom_target(patching_ya_music)
    add_dependencies(patching_ya_music AsarFusePatcher)
endif()
