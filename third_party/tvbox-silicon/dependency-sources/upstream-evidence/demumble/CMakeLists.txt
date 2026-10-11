cmake_minimum_required(VERSION 3.2.0 FATAL_ERROR)
project(demumble CXX)

find_package(JNI REQUIRED)
include_directories(${JAVA_INCLUDE_PATH})
include_directories(${JAVA_INCLUDE_PATH2})

set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -DSWIFT_STDLIB_HAS_TYPE_PRINTING")

if (UNIX)
  set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -Wall -fno-exceptions -fno-rtti")
  set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -fvisibility=hidden")
  if (${CMAKE_GENERATOR} STREQUAL "Ninja")
    set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -fdiagnostics-color")
  endif()

  # 10.9 chosen somewhat arbitrary; it's the first target where clang defaults
  # to libc++ and ld64 defaults to stripping __TEXT,__eh_frame.
  if (APPLE)
    set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -mmacosx-version-min=10.9")
  else()
    set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -fno-PIC")
    set(CMAKE_EXE_LINKER_FLAGS  "${CMAKE_EXE_LINKER_FLAGS} -no-pie")
  endif()
endif()

if (WIN32)
  set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS}")
  add_definitions(-D_CRT_SECURE_NO_WARNINGS)  # The LLVM build sets this.

  # This is apparently the simplest way to statically link the CRT in CMake:
  string(TOUPPER "${CMAKE_BUILD_TYPE}" build)
  set(flag_var "CMAKE_CXX_FLAGS_${build}")
  if(${flag_var} MATCHES "/MD")
    string(REGEX REPLACE "/MD" "/MT" ${flag_var} "${${flag_var}}")
  endif()
endif()

include_directories(third_party/llvm/include)
include_directories(third_party/swift/include)
add_executable(demangle
               demumble.cc
               third_party/llvm/lib/Demangle/ItaniumDemangle.cpp
               third_party/llvm/lib/Demangle/MicrosoftDemangle.cpp
               third_party/llvm/lib/Demangle/MicrosoftDemangleNodes.cpp

        third_party/swift/lib/Demangling/Demangler.cpp
        third_party/swift/lib/Demangling/Context.cpp
        third_party/swift/lib/Demangling/ManglingUtils.cpp
        third_party/swift/lib/Demangling/NodeDumper.cpp
        third_party/swift/lib/Demangling/NodePrinter.cpp
        third_party/swift/lib/Demangling/OldDemangler.cpp
        third_party/swift/lib/Demangling/OldRemangler.cpp
        third_party/swift/lib/Demangling/Punycode.cpp
        third_party/swift/lib/Demangling/Remangler.cpp
        third_party/swift/lib/Demangling/Errors.cpp
        third_party/swift/lib/Demangling/CrashReporter.cpp
)
set_target_properties(demangle PROPERTIES CXX_STANDARD 14
                                          CXX_STANDARD_REQUIRED ON)
add_library(demumble SHARED
               demumble.cc
               third_party/llvm/lib/Demangle/ItaniumDemangle.cpp
               third_party/llvm/lib/Demangle/MicrosoftDemangle.cpp
               third_party/llvm/lib/Demangle/MicrosoftDemangleNodes.cpp

        third_party/swift/lib/Demangling/Demangler.cpp
        third_party/swift/lib/Demangling/Context.cpp
        third_party/swift/lib/Demangling/ManglingUtils.cpp
        third_party/swift/lib/Demangling/NodeDumper.cpp
        third_party/swift/lib/Demangling/NodePrinter.cpp
        third_party/swift/lib/Demangling/OldDemangler.cpp
        third_party/swift/lib/Demangling/OldRemangler.cpp
        third_party/swift/lib/Demangling/Punycode.cpp
        third_party/swift/lib/Demangling/Remangler.cpp
        third_party/swift/lib/Demangling/Errors.cpp
        third_party/swift/lib/Demangling/CrashReporter.cpp
)
set_target_properties(demumble PROPERTIES CXX_STANDARD 14
                                          CXX_STANDARD_REQUIRED ON)
