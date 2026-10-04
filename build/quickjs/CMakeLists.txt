cmake_minimum_required(VERSION 3.20)
project(vodbox_quickjs C)
include(FetchContent)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
FetchContent_Declare(quickjs
  URL https://github.com/quickjs-ng/quickjs/archive/3c9afc9943323ee9c7dbd123c0cd991448f4b6c2.tar.gz
  URL_HASH SHA256=0af6f985be7030a7a9d12b92c25c2bc9f7dd716829c5074a81d7c4a4452040c8
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_MakeAvailable(quickjs)
add_library(vodbox_quickjs SHARED bridge.c)
target_include_directories(vodbox_quickjs PRIVATE ${quickjs_SOURCE_DIR})
target_link_libraries(vodbox_quickjs PRIVATE qjs)
set_target_properties(vodbox_quickjs PROPERTIES C_VISIBILITY_PRESET hidden)
