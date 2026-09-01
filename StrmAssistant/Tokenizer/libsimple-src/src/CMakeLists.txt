cmake_minimum_required(VERSION 3.2)
project(simple CXX)

# ★ StrmAssistant 集成改造：cppjieba 已随源码一起纳入仓库（libsimple-src/cppjieba），
#   不再从 GitHub 下载，保证离线可重复编译。
if(SIMPLE_WITH_JIEBA)
  set(CPPJIEBA_ROOT ${CMAKE_CURRENT_SOURCE_DIR}/../cppjieba)
  if(NOT EXISTS ${CPPJIEBA_ROOT}/include)
    message(FATAL_ERROR "cppjieba not found at ${CPPJIEBA_ROOT}")
  endif()
  set(source_dir ${CPPJIEBA_ROOT})
endif()

set(SOURCE_FILES
    pinyin.h
    simple_highlight.h
    simple_tokenizer.h
    pinyin.cc
    simple_highlight.cc
    simple_tokenizer.cc
    entry.cc
)

OPTION(BUILD_STATIC "Option to build static lib" OFF)
if (IOS OR BUILD_STATIC)
# iOS only support static library.
add_library(simple STATIC ${SOURCE_FILES})
else()
add_library(simple SHARED ${SOURCE_FILES})
endif()

if(SIMPLE_WITH_JIEBA)
  # 本地集成的 cppjieba（纯头文件库，无需构建）
  include_directories(${SQLITE3_HEADERS_DIR} ${source_dir}/include ${source_dir}/deps)
  target_include_directories(simple INTERFACE ${SQLITE3_HEADERS_DIR} ${source_dir}/include ${source_dir}/deps)
  # 把 jieba 词典拷到构建产物附近，便于运行时指定（原 ExternalProject 的 PRE_BUILD 拷贝）
  add_custom_command(TARGET simple POST_BUILD
                     COMMAND ${CMAKE_COMMAND} -E copy_directory
                     ${source_dir}/dict/ $<TARGET_FILE_DIR:simple>/dict/)
  INSTALL(DIRECTORY ${source_dir}/dict/ DESTINATION bin/dict FILES_MATCHING PATTERN "*.utf8")
else()
  include_directories(${SQLITE3_HEADERS_DIR})
  target_include_directories(simple INTERFACE ${SQLITE3_HEADERS_DIR})
endif()

if(BUILD_SQLITE3)
  target_link_libraries(simple PUBLIC coverage_config PRIVATE PINYIN_TEXT SQLite3)
else()
  target_link_libraries(simple PUBLIC coverage_config PRIVATE PINYIN_TEXT)
endif()

install(TARGETS simple DESTINATION bin)
