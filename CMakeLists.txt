cmake_minimum_required(VERSION 3.20)

cmake_policy(SET CMP0091 NEW)

project(TerminalRouter LANGUAGES C CXX)

set(
    CMAKE_MSVC_RUNTIME_LIBRARY
    "MultiThreaded$<$<CONFIG:Debug>:Debug>"
)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_subdirectory(deps/Remedy "${CMAKE_BINARY_DIR}/remedy" EXCLUDE_FROM_ALL)

add_library(terminal_remedy_adapter STATIC
    src/native/remedy/remedy_adapter.cpp
)
target_include_directories(terminal_remedy_adapter PUBLIC
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
)
target_link_libraries(terminal_remedy_adapter PUBLIC
    remedy_headers
    remedy_worker_lifecycle
)

add_library(terminal_router_client STATIC
    src/native/router/router_client.cpp
)
target_include_directories(terminal_router_client PUBLIC
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
)
target_link_libraries(terminal_router_client PUBLIC
    remedy_headers
    terminal_remedy_adapter
)

if(WIN32)
    set(TERMINAL_DOTNET "" CACHE FILEPATH "Path to the dotnet executable")
    if(NOT TERMINAL_DOTNET)
        find_program(TERMINAL_DOTNET NAMES dotnet REQUIRED)
    endif()

    set(
        TERMINAL_ROUTER_PROJECT
        "${CMAKE_CURRENT_SOURCE_DIR}/src/Terminal.Router/Terminal.Router.csproj"
    )
    set(TERMINAL_ROUTER_WIN_DIR "${CMAKE_BINARY_DIR}/router/win-x64")
    set(TERMINAL_ROUTER_WIN_EXE "${TERMINAL_ROUTER_WIN_DIR}/terminal-router.exe")
    file(GLOB TERMINAL_ROUTER_MANAGED_SOURCES CONFIGURE_DEPENDS
        "${CMAKE_CURRENT_SOURCE_DIR}/src/Terminal.Router/*.cs"
    )
    add_custom_command(
        OUTPUT "${TERMINAL_ROUTER_WIN_EXE}"
        COMMAND "${TERMINAL_DOTNET}" publish "${TERMINAL_ROUTER_PROJECT}"
                -c Release -r win-x64 --output "${TERMINAL_ROUTER_WIN_DIR}"
        DEPENDS "${TERMINAL_ROUTER_PROJECT}" ${TERMINAL_ROUTER_MANAGED_SOURCES}
        VERBATIM
    )
    add_custom_target(
        terminal_router_nativeaot ALL
        DEPENDS "${TERMINAL_ROUTER_WIN_EXE}"
    )

    add_library(terminal_router_pty SHARED
        src/Terminal.Router/native/android/terminal_router_pty.c
    )
    set_target_properties(terminal_router_pty PROPERTIES
        OUTPUT_NAME "terminal_router_pty"
        RUNTIME_OUTPUT_DIRECTORY "${TERMINAL_ROUTER_WIN_DIR}"
        RUNTIME_OUTPUT_DIRECTORY_DEBUG "${TERMINAL_ROUTER_WIN_DIR}"
        RUNTIME_OUTPUT_DIRECTORY_RELEASE "${TERMINAL_ROUTER_WIN_DIR}"
    )
    add_dependencies(terminal_router_nativeaot terminal_router_pty)
endif()
